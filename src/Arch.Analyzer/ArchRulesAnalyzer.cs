using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Arch.Analyzer
{
    /// <summary>
    /// O motor. Não contém nenhuma regra de arquitetura: lê a política do consumidor de
    /// <c>arch-rules.yaml</c> (AdditionalFiles), resolve camadas e roteia cada regra ao
    /// <see cref="IRuleEvaluator"/> do seu `type`, sempre reportando através de um slot
    /// pré-declarado do pool (ADR-001 §3, Opção C).
    ///
    /// <para><b>Ordem das coisas (ADR-001 §1 e §5):</b> <see cref="SupportedDiagnostics"/> é lida sem
    /// contexto de compilação e cacheada pela sessão da IDE inteira — por isso ela devolve o pool
    /// fixo de <see cref="ArchDiagnostics"/>, e nunca algo derivado do YAML. O YAML só existe dentro
    /// de <c>RegisterCompilationStartAction</c>, onde <c>context.Options.AdditionalFiles</c> está
    /// disponível.</para>
    ///
    /// <para><b>Custo por compilação:</b> sem <c>arch-rules.yaml</c> nos AdditionalFiles nenhuma ação
    /// é registrada (saída rápida); com ele, o parse acontece uma vez por INSTÂNCIA de
    /// <see cref="SourceText"/> — a IDE reaproveita essa instância entre compilações enquanto o
    /// arquivo não muda, então digitar código não reparseia a política.</para>
    ///
    /// <para><b><c>extends:</c> (ADR-001 D2):</b> a cadeia é resolvida aqui, contra
    /// <c>context.Options.AdditionalFiles</c> e SÓ contra eles — um analyzer não pode fazer I/O de
    /// disco (RS1035), então o que não foi passado pelo MSBuild simplesmente não existe para ele. O
    /// <c>build/Arch.Analyzer.targets</c> do pacote inclui por convenção <c>arch-rules.yaml</c> e
    /// <c>arch-rules.*.yaml</c> do diretório do projeto; um `extends` fora dessa convenção vira
    /// <c>ARCH9006</c> (warning, NÃO bloqueante — ver <see cref="ResolveChain"/>).</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ArchRulesAnalyzer : DiagnosticAnalyzer
    {
        private const string ConfigFileName = "arch-rules.yaml";

        /// <summary>
        /// Limites da resolução de `extends`. A detecção de ciclo já garante terminação (o caminho
        /// da raiz até o nó corrente é acíclico e o conjunto de AdditionalFiles é finito); estes
        /// limites cobrem o resto: recursão profunda demais para a pilha do host e grafos em
        /// diamante, onde o número de TEXTOS na cadeia pode crescer exponencialmente mesmo sem
        /// nenhum ciclo.
        /// </summary>
        private const int MaxChainDepth = 32;

        private const int MaxChainTexts = 64;

        private readonly IReadOnlyDictionary<string, IRuleEvaluator> _evaluators;

        /// <summary>
        /// Fronteira com o parser de <c>Arch.Config</c>. É um delegate (e não a chamada direta) só
        /// para que os testes do pipeline possam injetar uma configuração determinística; em
        /// produção aponta sempre para
        /// <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/> — o overload de CADEIA, que
        /// recebe os textos em ordem crescente de precedência (base primeiro, o arquivo que faz
        /// `extends` por último).
        /// </summary>
        private readonly Func<IReadOnlyList<string>, ArchConfigParseResult> _parse;

        /// <summary>
        /// Serializa a substituição de uma entrada do cache (Remove + Add não é atômico e
        /// <c>ConditionalWeakTable.Add</c> lança se a chave já existir). Necessário porque
        /// <c>EnableConcurrentExecution</c> está ligado e a mesma instância do analyzer atende
        /// compilações concorrentes na IDE.
        /// </summary>
        private readonly object _cacheWriteGate = new object();

        /// <summary>
        /// Cache do resultado do parse, chaveado pela INSTÂNCIA do <see cref="SourceText"/> do
        /// AdditionalFile (ADR-001 §5). Deliberadamente NÃO por caminho (não reflete o conteúdo) e
        /// NÃO por <c>SourceText.GetChecksum()</c> — que devolve <c>ImmutableArray&lt;byte&gt;</c>,
        /// um struct, inválido como chave de <see cref="ConditionalWeakTable{TKey,TValue}"/> (exige
        /// <c>class</c> e compara por referência) e desaconselhado para igualdade pela própria
        /// documentação (BOM/encoding mudam o checksum de conteúdos idênticos).
        ///
        /// <para>A tabela é campo de instância, não estática: o host reaproveita a instância do
        /// analyzer durante toda a sessão (é o que dá o efeito de cache entre compilações), e assim
        /// dois analyzers configurados de formas diferentes nunca compartilham resultado. Sendo
        /// "weak" nas chaves, a entrada morre junto com o <see cref="SourceText"/> — nada segura
        /// memória depois que a IDE descarta o texto.</para>
        /// </summary>
        private readonly ConditionalWeakTable<SourceText, CachedConfig> _configCache =
            new ConditionalWeakTable<SourceText, CachedConfig>();

        /// <summary>Construtor usado pelo host do compilador (Roslyn exige um sem parâmetros).</summary>
        public ArchRulesAnalyzer()
            : this(RuleEvaluatorRegistry.Default, (Func<IReadOnlyList<string>, ArchConfigParseResult>)null)
        {
        }

        /// <summary>
        /// Seam de teste de UM ÚNICO TEXTO, preservado para os testes que injetam uma
        /// <see cref="ArchConfig"/> fixa ignorando o conteúdo do YAML. A resolução da cadeia de
        /// `extends` continua acontecendo normalmente (inclusive ARCH9006/ARCH9007), mas o MERGE
        /// entre documentos não — o stub recebe o texto de MAIOR precedência, que é o arquivo
        /// principal (último da lista, ver
        /// <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/>). Quem quiser exercitar o
        /// merge real deve usar o construtor público (parser de produção) ou o overload de cadeia.
        /// </summary>
        internal ArchRulesAnalyzer(
            IReadOnlyDictionary<string, IRuleEvaluator> evaluators,
            Func<string, ArchConfigParseResult> parse)
            : this(
                evaluators,
                parse == null
                    ? (Func<IReadOnlyList<string>, ArchConfigParseResult>)null
                    : texts => parse(texts[texts.Count - 1]))
        {
        }

        internal ArchRulesAnalyzer(
            IReadOnlyDictionary<string, IRuleEvaluator> evaluators,
            Func<IReadOnlyList<string>, ArchConfigParseResult> parse)
        {
            _evaluators = evaluators ?? RuleEvaluatorRegistry.Empty;
            _parse = parse ?? ArchConfigParser.Parse;
        }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ArchDiagnostics.All; }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var additionalFile = FindConfigFile(context.Options.AdditionalFiles);
            if (additionalFile == null)
            {
                // Saída rápida (ADR-001 §5): quem instalou o pacote e ainda não configurou nada não
                // paga por ação nenhuma registrada.
                return;
            }

            var sourceText = additionalFile.GetText(context.CancellationToken);
            if (sourceText == null)
            {
                return;
            }

            var cached = GetOrCreateConfig(
                context.Options.AdditionalFiles, additionalFile, sourceText, context.CancellationToken);

            var engineDiagnostics = new List<Diagnostic>();

            foreach (var error in cached.Errors)
            {
                if (error == null)
                {
                    continue;
                }

                engineDiagnostics.Add(CreateEngineDiagnostic(additionalFile, sourceText, error));
            }

            if (cached.HasBlockingError || cached.Config == null)
            {
                // Config inválida invalida tudo: nenhuma ação de regra é registrada, para não
                // transformar um YAML malformado numa avalanche de falsos positivos (ADR-001 D4).
                ReportAtCompilationEnd(context, engineDiagnostics);
                return;
            }

            ImmutableArray<string> unresolvedTypeNames;
            var layerResolver = LayerResolver.Create(cached.Config, context.Compilation, out unresolvedTypeNames);

            foreach (var name in unresolvedTypeNames)
            {
                // Uma vez por NOME, no compilation-start — nunca por símbolo (ADR-001 §5).
                engineDiagnostics.Add(CreateEngineDiagnostic(
                    additionalFile,
                    sourceText,
                    ArchDiagnostics.Arch9004,
                    "Tipo '" + name + "' referenciado em arch-rules.yaml não foi encontrado nas referências desta " +
                    "compilação; o critério que o usa foi ignorado."));
            }

            var slotsInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in cached.Config.Rules)
            {
                if (rule == null || !rule.Enabled)
                {
                    // `enabled: false` desliga a regra preservando rastreabilidade (ADR-001 D2).
                    continue;
                }

                DiagnosticDescriptor slotDescriptor;
                if (!ArchDiagnostics.TryGetSlot(rule.Slot, out slotDescriptor))
                {
                    engineDiagnostics.Add(CreateEngineDiagnostic(
                        additionalFile,
                        sourceText,
                        ArchDiagnostics.Arch9002,
                        "Regra '" + rule.Id + "' ignorada: slot '" + rule.Slot + "' está fora do pool pré-declarado " +
                        "(ARCH0001..ARCH" + ArchDiagnostics.SlotCount.ToString("D4", CultureInfo.InvariantCulture) + ")."));
                    continue;
                }

                if (!slotsInUse.Add(slotDescriptor.Id))
                {
                    engineDiagnostics.Add(CreateEngineDiagnostic(
                        additionalFile,
                        sourceText,
                        ArchDiagnostics.Arch9002,
                        "Regra '" + rule.Id + "' ignorada: o slot '" + slotDescriptor.Id + "' já está vinculado a " +
                        "outra regra. Dois IDs de negócio no mesmo slot tornariam impossível dar override de " +
                        "severidade a um sem afetar o outro."));
                    continue;
                }

                IRuleEvaluator evaluator;
                if (!_evaluators.TryGetValue(rule.Type, out evaluator) || evaluator == null)
                {
                    // ADR-001 D4 (MINOR aditivo): tipo desconhecido é ARCH9003 e não derruba o resto.
                    engineDiagnostics.Add(CreateEngineDiagnostic(
                        additionalFile,
                        sourceText,
                        ArchDiagnostics.Arch9003,
                        "Regra '" + rule.Id + "' ignorada: tipo de regra '" + rule.Type + "' não é suportado por esta " +
                        "versão da lib."));
                    continue;
                }

                evaluator.RegisterActions(context, rule, layerResolver, slotDescriptor);
            }

            ReportAtCompilationEnd(context, engineDiagnostics);
        }

        private static AdditionalText FindConfigFile(ImmutableArray<AdditionalText> additionalFiles)
        {
            // Sem LINQ: este trecho roda em toda compilação, inclusive nas que não têm config.
            for (var i = 0; i < additionalFiles.Length; i++)
            {
                var file = additionalFiles[i];
                if (file != null
                    && string.Equals(Path.GetFileName(file.Path), ConfigFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }

            return null;
        }

        /// <summary>
        /// Resultado do cache com validação da CADEIA inteira. Chavear só pelo
        /// <see cref="SourceText"/> do arquivo principal não basta desde que `extends` existe: editar
        /// o `arch-rules.base.yaml` não muda a instância de texto do `arch-rules.yaml`, e a entrada
        /// cacheada ficaria descrevendo uma política que não existe mais. A validação é de
        /// identidade de instância (mesma premissa do cache, ADR-001 §5) e custa uma varredura de
        /// @(AdditionalFiles) por arquivo da cadeia — no caso comum (sem `extends`), UM.
        /// </summary>
        private CachedConfig GetOrCreateConfig(
            ImmutableArray<AdditionalText> additionalFiles,
            AdditionalText mainFile,
            SourceText mainText,
            CancellationToken cancellationToken)
        {
            CachedConfig cached;
            if (_configCache.TryGetValue(mainText, out cached)
                && cached.IsStillValid(additionalFiles, cancellationToken))
            {
                return cached;
            }

            var fresh = ResolveAndParse(additionalFiles, mainFile, mainText, cancellationToken);

            lock (_cacheWriteGate)
            {
                _configCache.Remove(mainText);
                _configCache.Add(mainText, fresh);
            }

            return fresh;
        }

        private CachedConfig ResolveAndParse(
            ImmutableArray<AdditionalText> additionalFiles,
            AdditionalText mainFile,
            SourceText mainText,
            CancellationToken cancellationToken)
        {
            var chain = new ChainResolution();

            try
            {
                ResolveChain(additionalFiles, mainFile, mainText, NormalizePath(mainFile.Path), chain, 0, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return CachedConfig.Blocking(
                    "Falha inesperada ao resolver a cadeia de 'extends' de arch-rules.yaml: "
                        + ex.GetType().Name + ": " + ex.Message,
                    chain);
            }

            try
            {
                var result = _parse(chain.Texts);

                if (result == null)
                {
                    return CachedConfig.Blocking(
                        "O parser de arch-rules.yaml não devolveu resultado algum (retorno nulo).", chain);
                }

                // Os problemas de RESOLUÇÃO vêm antes dos de SCHEMA: eles explicam por que um trecho
                // da política pode estar faltando quando o erro de schema for lido.
                return new CachedConfig(result.Config, chain.CombineIssuesWith(result.Errors), chain);
            }
            catch (OperationCanceledException)
            {
                // Cancelamento é fluxo normal na IDE (o usuário digitou de novo): nunca vira
                // diagnóstico, e nunca pode ser cacheado como se fosse resultado.
                throw;
            }
            catch (Exception ex)
            {
                // Uma exceção atravessando a fronteira do analyzer vira AD0001 — mensagem inútil
                // para quem só quer saber que o YAML está quebrado. Converte-se no erro de motor
                // correspondente, que é acionável e silenciável.
                return CachedConfig.Blocking(
                    "Falha inesperada ao interpretar arch-rules.yaml: " + ex.GetType().Name + ": " + ex.Message,
                    chain);
            }
        }

        /// <summary>
        /// Percorre a cadeia de `extends` em profundidade e PÓS-ORDEM: cada arquivo estendido entra
        /// na lista antes de quem o estende, o que produz exatamente a ordem crescente de precedência
        /// exigida por <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/> — "o arquivo que
        /// faz extends vence" (ADR-001 D2). Mesma semântica de <c>Arch.Cli.ExtendsResolver</c>, que
        /// resolve do disco; a diferença é a fonte: aqui, só @(AdditionalFiles).
        ///
        /// <para><b>Decisão de design — `extends` ausente NÃO é bloqueante.</b> Um caminho sem
        /// AdditionalFile correspondente vira ARCH9006 (warning) e o RAMO é descartado; a lista de
        /// textos segue com o arquivo principal e com as camadas que foram encontradas, e as regras
        /// que não dependiam da camada ausente continuam sendo avaliadas normalmente. A alternativa
        /// (tratar como config inválida, ARCH9001) foi rejeitada: ela transformaria um `.csproj` sem
        /// uma linha de AdditionalFiles em ZERO diagnóstico — exatamente o modo de falha silenciosa
        /// que esta correção existe para eliminar. O custo aceito é que uma regra cujo `layer` só
        /// existia no arquivo ausente deixa de casar qualquer símbolo (não reporta nada), e o
        /// ARCH9006 é o que explica isso ao consumidor.</para>
        ///
        /// <para>Caminhos são comparados como STRING normalizada (separadores unificados, `.`/`..`
        /// resolvidos lexicalmente, comparação case-insensitive) — nunca via <c>File.Exists</c> ou
        /// <c>Path.GetFullPath</c>, que tocariam o disco e violariam RS1035.</para>
        /// </summary>
        private void ResolveChain(
            ImmutableArray<AdditionalText> additionalFiles,
            AdditionalText file,
            SourceText text,
            string normalizedPath,
            ChainResolution chain,
            int depth,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            chain.Stack.Add(normalizedPath);
            chain.Files.Add(new ChainFile(file.Path, text));

            var content = text.ToString();

            foreach (var relative in ArchConfigParser.PeekExtends(content))
            {
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                var target = ResolveRelativeTo(normalizedPath, relative);

                if (ContainsPath(chain.Stack, target))
                {
                    chain.Issues.Add(Issue(
                        "ARCH9007",
                        "Ciclo de 'extends' detectado: " + string.Join(" -> ", chain.Stack) + " -> " + target +
                        ". O ramo foi cortado; as demais camadas continuam valendo."));
                    continue;
                }

                if (depth + 1 >= MaxChainDepth || chain.Texts.Count >= MaxChainTexts)
                {
                    chain.Issues.Add(Issue(
                        "ARCH9007",
                        "Cadeia de 'extends' longa demais ao tentar resolver '" + relative + "' a partir de '" +
                        normalizedPath + "' (limite: " + MaxChainDepth + " níveis / " + MaxChainTexts +
                        " arquivos). O ramo foi cortado."));
                    continue;
                }

                var found = FindByNormalizedPath(additionalFiles, target);
                if (found == null)
                {
                    chain.MissingPaths.Add(target);
                    chain.Issues.Add(Issue(
                        "ARCH9006",
                        "Arquivo de extends '" + relative + "' declarado em '" + Path.GetFileName(normalizedPath) +
                        "' (resolvido para '" + target + "') não foi encontrado nos AdditionalFiles — adicione-o " +
                        "manualmente ao .csproj (<AdditionalFiles Include=\"...\" />) ou siga a convenção de nome " +
                        "`arch-rules.*.yaml` no diretório do projeto. A camada foi ignorada; as demais regras " +
                        "continuam valendo."));
                    continue;
                }

                var foundText = found.GetText(cancellationToken);
                if (foundText == null)
                {
                    chain.MissingPaths.Add(target);
                    chain.Issues.Add(Issue(
                        "ARCH9006",
                        "Arquivo de extends '" + relative + "' está em AdditionalFiles ('" + found.Path +
                        "') mas o host não devolveu texto para ele. A camada foi ignorada; as demais regras " +
                        "continuam valendo."));
                    continue;
                }

                ResolveChain(additionalFiles, found, foundText, target, chain, depth + 1, cancellationToken);
            }

            chain.Texts.Add(content);
            chain.Stack.RemoveAt(chain.Stack.Count - 1);
        }

        private static SchemaValidationError Issue(string code, string message)
        {
            // Sempre não bloqueante e sem linha/coluna: PeekExtends devolve os caminhos, não as
            // posições deles no YAML, então o diagnóstico ancora no início do arquivo principal.
            return new SchemaValidationError(code, message, 0, 0, isBlocking: false);
        }

        /// <summary>
        /// Acha o AdditionalFile que corresponde a <paramref name="normalizedPath"/>. Duas passadas:
        ///
        /// <para>1. Igualdade exata do caminho normalizado — o caso normal, inclusive quando tudo
        /// está relativo ao diretório do projeto (é assim que o MSBuild costuma entregar os
        /// AdditionalFiles que ficam DENTRO da pasta do projeto; verificado em build real).</para>
        ///
        /// <para>2. Fallback deliberadamente estreito para ROOTEDNESS MISTA: o alvo é relativo e o
        /// candidato absoluto (ou vice-versa) — o que acontece quando o `arch-rules.yaml` chega
        /// relativizado e um `extends` compartilhado, fora da pasta do projeto, chega absoluto. Sem
        /// o diretório do projeto (que o analyzer não tem como descobrir sem I/O) os dois não podem
        /// ser alinhados; o que dá para afirmar é que o absoluto TERMINA com a cauda do relativo. Só
        /// vale se UM ÚNICO candidato satisfizer isso — havendo ambiguidade, é melhor o ARCH9006
        /// acionável do que herdar silenciosamente a política errada. Pelo mesmo motivo o fallback
        /// não é usado quando ambos são relativos ou ambos absolutos: aí a igualdade exata é a
        /// resposta certa, e casar "pelo fim" faria um homônimo de outro diretório passar por
        /// legítimo.</para>
        /// </summary>
        private static AdditionalText FindByNormalizedPath(ImmutableArray<AdditionalText> additionalFiles, string normalizedPath)
        {
            for (var i = 0; i < additionalFiles.Length; i++)
            {
                var candidate = additionalFiles[i];
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(NormalizePath(candidate.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            AdditionalText onlyMatch = null;

            for (var i = 0; i < additionalFiles.Length; i++)
            {
                var candidate = additionalFiles[i];
                if (candidate == null || !IsMixedRootednessSuffixMatch(normalizedPath, NormalizePath(candidate.Path)))
                {
                    continue;
                }

                if (onlyMatch != null)
                {
                    return null;
                }

                onlyMatch = candidate;
            }

            return onlyMatch;
        }

        private static bool IsMixedRootednessSuffixMatch(string target, string candidate)
        {
            var targetRooted = IsRooted(target);
            if (targetRooted == IsRooted(candidate))
            {
                return false;
            }

            var absolute = targetRooted ? target : candidate;
            var relative = targetRooted ? candidate : target;

            // A cauda do relativo é o que sobra depois dos ".." que o normalizador preservou — eles
            // descrevem um trecho do caminho que só o diretório do projeto conhece.
            while (relative.StartsWith("../", StringComparison.Ordinal))
            {
                relative = relative.Substring(3);
            }

            return relative.Length > 0
                && absolute.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRooted(string normalizedPath)
        {
            return normalizedPath.StartsWith("/", StringComparison.Ordinal) || HasDriveLetter(normalizedPath);
        }

        private static bool ContainsPath(List<string> paths, string path)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (string.Equals(paths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolve <paramref name="relative"/> contra o DIRETÓRIO de
        /// <paramref name="normalizedSourcePath"/> (o arquivo que declarou o `extends`, não a raiz da
        /// cadeia — um arquivo estendido pode estender outro e o relativo reancora a cada nível).
        /// Puramente textual: nenhum acesso a disco.
        /// </summary>
        private static string ResolveRelativeTo(string normalizedSourcePath, string relative)
        {
            var normalizedRelative = relative.Replace('\\', '/').Trim();

            // Caminho já absoluto (POSIX "/x/y" ou Windows "C:/x/y") não reancora em nada.
            if (normalizedRelative.StartsWith("/", StringComparison.Ordinal) || HasDriveLetter(normalizedRelative))
            {
                return NormalizePath(normalizedRelative);
            }

            var directory = GetDirectory(normalizedSourcePath);

            return NormalizePath(directory.Length == 0 ? normalizedRelative : directory + "/" + normalizedRelative);
        }

        private static bool HasDriveLetter(string path)
        {
            return path.Length >= 2
                && path[1] == ':'
                && ((path[0] >= 'a' && path[0] <= 'z') || (path[0] >= 'A' && path[0] <= 'Z'));
        }

        private static string GetDirectory(string normalizedPath)
        {
            var lastSlash = normalizedPath.LastIndexOf('/');
            return lastSlash < 0 ? string.Empty : normalizedPath.Substring(0, lastSlash);
        }

        /// <summary>
        /// Normalização LEXICAL de caminho, a única disponível dentro de um analyzer: unifica
        /// separadores (`\` → `/`), descarta segmentos vazios e `.`, e resolve `..` sem tocar o
        /// disco. <c>Path.GetFullPath</c> não serve — além de depender do diretório corrente do
        /// processo (que num build é o do MSBuild, não o do projeto), ele é I/O de arquivo na
        /// perspectiva do RS1035.
        /// </summary>
        internal static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var unified = path.Replace('\\', '/');
            var isRooted = unified[0] == '/';
            var segments = unified.Split('/');
            var resolved = new List<string>(segments.Length);

            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (resolved.Count > 0 && resolved[resolved.Count - 1] != "..")
                    {
                        resolved.RemoveAt(resolved.Count - 1);
                    }
                    else if (!isRooted)
                    {
                        // ".." acima da raiz relativa precisa ser preservado, senão "../a" e "a"
                        // colidiriam.
                        resolved.Add("..");
                    }

                    continue;
                }

                resolved.Add(segment);
            }

            var joined = string.Join("/", resolved.ToArray());
            return isRooted ? "/" + joined : joined;
        }

        // RS1013 ("este compilation-start só registra um compilation-end; use
        // RegisterCompilationAction") é um falso positivo aqui, e a análise dele é lexical — ela
        // enxerga só este método auxiliar. O compilation-start REAL (OnCompilationStart) também
        // registra as ações de cada regra, via IRuleEvaluator.RegisterActions. Trocar por
        // RegisterCompilationAction também não seria possível: o contexto de compilation-start não
        // expõe esse registro, e os erros de motor precisam ser calculados exatamente ali, onde a
        // config já foi resolvida uma única vez. A supressão cobre a assinatura inteira porque é no
        // parâmetro `context` que o diagnóstico é reportado.
#pragma warning disable RS1013
        private static void ReportAtCompilationEnd(CompilationStartAnalysisContext context, List<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return;
            }

            // Os diagnósticos já estão prontos (criados uma única vez no compilation-start); o
            // compilation-end só existe porque o contexto de start não expõe ReportDiagnostic.
            context.RegisterCompilationEndAction(endContext =>
            {
                foreach (var diagnostic in diagnostics)
                {
                    endContext.ReportDiagnostic(diagnostic);
                }
            });
        }
#pragma warning restore RS1013

        private static Diagnostic CreateEngineDiagnostic(
            AdditionalText file,
            SourceText sourceText,
            SchemaValidationError error)
        {
            var descriptor = ArchDiagnostics.GetEngineDescriptorOrFallback(error.Code, error.IsBlocking);

            // Código desconhecido (parser mais novo que o analyzer) não pode ser reportado como ele
            // mesmo — não está em SupportedDiagnostics — mas não pode sumir da mensagem também.
            var message = ArchDiagnostics.IsKnownEngineCode(error.Code)
                ? error.Message
                : error.Code + ": " + error.Message;

            return Diagnostic.Create(
                descriptor,
                CreateLocation(file, sourceText, error.Line, error.Column),
                message);
        }

        private static Diagnostic CreateEngineDiagnostic(
            AdditionalText file,
            SourceText sourceText,
            DiagnosticDescriptor descriptor,
            string message)
        {
            return Diagnostic.Create(descriptor, CreateLocation(file, sourceText, 0, 0), message);
        }

        /// <summary>
        /// Localização dentro do próprio <c>arch-rules.yaml</c> — um erro de configuração precisa
        /// abrir o arquivo de configuração, não um .cs qualquer (ADR-001 D4: "apontando
        /// linha/coluna"). Linha/coluna inválidas ou ausentes caem para o início do arquivo.
        /// </summary>
        private static Location CreateLocation(AdditionalText file, SourceText sourceText, int line, int column)
        {
            var lines = sourceText.Lines;
            TextSpan span;

            if (line <= 0 || lines.Count == 0)
            {
                span = new TextSpan(0, 0);
            }
            else
            {
                var textLine = lines[Math.Min(line - 1, lines.Count - 1)];
                var start = textLine.Start + Math.Max(0, column - 1);
                start = Math.Min(start, textLine.End);
                span = TextSpan.FromBounds(start, textLine.End);
            }

            return Location.Create(file.Path, span, sourceText.Lines.GetLinePositionSpan(span));
        }

        /// <summary>
        /// O que fica no cache: a config resolvida MAIS os erros do parse. Guardar só a
        /// <see cref="ArchConfig"/> faria os erros não bloqueantes (ARCH9003 de uma regra, por
        /// exemplo) aparecerem na primeira compilação e sumirem em todas as seguintes, que acertam o
        /// cache — diagnóstico intermitente é pior que diagnóstico nenhum.
        /// </summary>
        private sealed class CachedConfig
        {
            internal readonly ArchConfig Config;
            internal readonly IReadOnlyList<SchemaValidationError> Errors;
            internal readonly bool HasBlockingError;

            /// <summary>Arquivos (caminho + instância de texto) que produziram esta config.</summary>
            private readonly IReadOnlyList<ChainFile> _chainFiles;

            /// <summary>
            /// Caminhos de `extends` que NÃO tinham AdditionalFile na resolução. Se algum passar a
            /// existir, a entrada está obsoleta mesmo que todo texto da cadeia continue idêntico —
            /// caso típico de quem lê o ARCH9006 e acrescenta a linha que faltava no .csproj.
            /// </summary>
            private readonly IReadOnlyList<string> _missingPaths;

            internal CachedConfig(ArchConfig config, IReadOnlyList<SchemaValidationError> errors, ChainResolution chain)
            {
                Config = config;
                Errors = errors ?? new SchemaValidationError[0];
                _chainFiles = chain != null ? (IReadOnlyList<ChainFile>)chain.Files : new ChainFile[0];
                _missingPaths = chain != null ? (IReadOnlyList<string>)chain.MissingPaths : new string[0];

                for (var i = 0; i < Errors.Count; i++)
                {
                    if (Errors[i] != null && Errors[i].IsBlocking)
                    {
                        HasBlockingError = true;
                        break;
                    }
                }
            }

            internal static CachedConfig Blocking(string message, ChainResolution chain)
            {
                return new CachedConfig(
                    null,
                    new[] { new SchemaValidationError("ARCH9001", message, 0, 0, true) },
                    chain);
            }

            internal bool IsStillValid(ImmutableArray<AdditionalText> additionalFiles, CancellationToken cancellationToken)
            {
                for (var i = 0; i < _chainFiles.Count; i++)
                {
                    var chainFile = _chainFiles[i];
                    var current = FindByExactPath(additionalFiles, chainFile.Path);

                    if (current == null || !ReferenceEquals(current.GetText(cancellationToken), chainFile.Text))
                    {
                        return false;
                    }
                }

                for (var i = 0; i < _missingPaths.Count; i++)
                {
                    if (FindByNormalizedPath(additionalFiles, _missingPaths[i]) != null)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static AdditionalText FindByExactPath(ImmutableArray<AdditionalText> additionalFiles, string path)
            {
                for (var i = 0; i < additionalFiles.Length; i++)
                {
                    var candidate = additionalFiles[i];
                    if (candidate != null && string.Equals(candidate.Path, path, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }

                return null;
            }
        }

        /// <summary>Um arquivo da cadeia, como ele estava quando a config foi resolvida.</summary>
        private struct ChainFile
        {
            internal readonly string Path;
            internal readonly SourceText Text;

            internal ChainFile(string path, SourceText text)
            {
                Path = path;
                Text = text;
            }
        }

        /// <summary>
        /// Estado mutável de uma resolução de cadeia: os textos já coletados em ordem de precedência,
        /// os arquivos que os produziram (para invalidar o cache), os caminhos de `extends` não
        /// encontrados e os problemas (ARCH9006/ARCH9007) a reportar. <see cref="Stack"/> é o caminho
        /// da raiz até o nó corrente — a base da detecção de ciclo.
        /// </summary>
        private sealed class ChainResolution
        {
            internal readonly List<string> Texts = new List<string>();
            internal readonly List<ChainFile> Files = new List<ChainFile>();
            internal readonly List<string> MissingPaths = new List<string>();
            internal readonly List<SchemaValidationError> Issues = new List<SchemaValidationError>();
            internal readonly List<string> Stack = new List<string>();

            internal IReadOnlyList<SchemaValidationError> CombineIssuesWith(IReadOnlyList<SchemaValidationError> parseErrors)
            {
                if (Issues.Count == 0)
                {
                    return parseErrors ?? (IReadOnlyList<SchemaValidationError>)new SchemaValidationError[0];
                }

                var combined = new List<SchemaValidationError>(Issues);
                if (parseErrors != null)
                {
                    combined.AddRange(parseErrors);
                }

                return combined;
            }
        }
    }
}
