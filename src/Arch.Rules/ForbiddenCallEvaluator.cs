using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Arch.Rules
{
    /// <summary>
    /// `forbidden-call` (ADR-001 D2): nenhum tipo da camada <c>from</c> pode alcançar diretamente um
    /// tipo da camada <c>to</c>. "Alcançar" aqui é, exatamente, o conjunto de quatro
    /// <see cref="OperationKind"/> fixado no ADR-001 §5 — <see cref="OperationKind.Invocation"/>,
    /// <see cref="OperationKind.ObjectCreation"/>, <see cref="OperationKind.PropertyReference"/> e
    /// <see cref="OperationKind.FieldReference"/>. Nenhuma varredura sintática de árvore completa:
    /// o custo por tecla digitada na IDE é o requisito não-funcional dominante.
    ///
    /// <para><b>Por que esta classe não é `sealed`.</b> `must-route-through` é, semanticamente,
    /// açúcar sintático sobre `forbidden-call` (ADR-001 D2, "Nota sobre must-route-through": a rota
    /// real A→B→C não é verificável sem o grafo de chamadas completo; o que se verifica é a proibição
    /// direta A→C, e `through` só alimenta a mensagem). A trilha 2B herda daqui e sobrescreve apenas
    /// <see cref="RuleType"/> e <see cref="DefaultMessageTemplate"/> — a lógica de detecção, o
    /// controle de falso positivo e a composição de mensagem são compartilhados, não copiados.</para>
    ///
    /// <para><b>Código gerado.</b> Não há filtro de código gerado aqui de propósito: o motor já
    /// chama <c>ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)</c> uma vez em
    /// <c>ArchRulesAnalyzer.Initialize</c> (ADR-001 §5), o que faz o próprio Roslyn nunca entregar
    /// operações de arquivos gerados a estas ações. Repetir a configuração por avaliador seria
    /// impossível (o contrato só recebe o contexto de compilation-start, que não expõe esse ajuste) e
    /// redundante.</para>
    /// </summary>
    public class ForbiddenCallEvaluator : IRuleEvaluator
    {
        /// <summary>Valor de `rules[].type` no YAML tratado por esta classe.</summary>
        public const string TypeName = "forbidden-call";

        /// <summary>
        /// Chaves de <see cref="RuleDefinition.Extra"/> onde o parser (trilha 1A) entrega as camadas.
        /// Confirmado contra <c>Arch.Config/Internal/ArchConfigDocumentParser.cs</c>: `from`/`to` estão
        /// em <c>KnownExtraKeys</c> e são serializados como escalares simples
        /// (<c>Extra["from"] = "Controller"</c>), num dicionário <see cref="StringComparer.Ordinal"/>.
        /// </summary>
        public const string FromKey = "from";

        /// <inheritdoc cref="FromKey"/>
        public const string ToKey = "to";

        // Chaves de Diagnostic.Properties. ADR-001 §3 (Opção C) exige que o ID de negócio da
        // organização chegue ao consumidor pela mensagem E por Properties — Properties é o canal
        // legível por máquina (Code Fix da Fase 3+, relatórios de CI), a mensagem é o canal humano.
        private const string RuleIdProperty = "archRuleId";
        private const string RuleTypeProperty = "archRuleType";
        private const string FromProperty = "archFrom";
        private const string ToProperty = "archTo";
        private const string HelpProperty = "archHelp";

        public virtual string RuleType
        {
            get { return TypeName; }
        }

        /// <summary>
        /// Template usado quando a regra não declara `message:` no YAML. Os mesmos placeholders de um
        /// template do consumidor valem aqui (ADR-001 D2 mostra
        /// <c>"{from} não pode chamar {to} diretamente ({symbol}). Passe por um Service."</c>).
        /// "acessar" e não "chamar" porque a regra também cobre leitura de propriedade/campo e
        /// construção de instância, não só invocação de método.
        /// </summary>
        protected virtual string DefaultMessageTemplate
        {
            get { return "{from} não pode acessar {to} diretamente ({symbol})."; }
        }

        public void RegisterActions(
            CompilationStartAnalysisContext context,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor)
        {
            if (context == null || rule == null || layerResolver == null || slotDescriptor == null)
            {
                return;
            }

            string fromLayer;
            string toLayer;

            // Regra sem `from`/`to` não tem semântica definida em D2 — e o avaliador não tem canal
            // para reportar erro de configuração (o contrato IRuleEvaluator não recebe nada capaz de
            // emitir ARCH9xxx). Registrar nada é a única saída segura: o oposto (assumir "qualquer
            // origem" para um `from` ausente) transformaria um YAML incompleto numa avalanche de
            // falsos positivos, exatamente o que ADR-001 D4 quer evitar. Ver "Riscos/pendências".
            if (!TryGetLayerName(rule, FromKey, out fromLayer) || !TryGetLayerName(rule, ToKey, out toLayer))
            {
                return;
            }

            // Tudo que não depende da operação é resolvido UMA vez por regra por compilação, não por
            // operação analisada: template escolhido e Properties já imutáveis.
            var template = string.IsNullOrWhiteSpace(rule.MessageTemplate)
                ? DefaultMessageTemplate
                : rule.MessageTemplate;

            var properties = BuildProperties(rule, fromLayer, toLayer);

            context.RegisterOperationAction(
                operationContext => Analyze(
                    operationContext, rule, layerResolver, slotDescriptor, fromLayer, toLayer, template, properties),
                OperationKind.Invocation,
                OperationKind.ObjectCreation,
                OperationKind.PropertyReference,
                OperationKind.FieldReference);
        }

        private void Analyze(
            OperationAnalysisContext operationContext,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor,
            string fromLayer,
            string toLayer,
            string template,
            ImmutableDictionary<string, string> properties)
        {
            var operation = operationContext.Operation;

            INamedTypeSymbol targetType;
            string symbolText;
            if (!TryGetTarget(operation, out targetType, out symbolText))
            {
                return;
            }

            // Ordem deliberada, do filtro mais seletivo e mais barato para o mais caro: a esmagadora
            // maioria das operações de uma compilação não toca a camada `to`, e IsInLayer é memoizado
            // por símbolo no LayerResolver real (ADR-001 §5).
            if (!layerResolver.IsInLayer(targetType, toLayer))
            {
                return;
            }

            var sourceType = GetContainingType(operationContext.ContainingSymbol);
            if (sourceType == null)
            {
                return;
            }

            // Controle de falso positivo nº 1: um tipo usando a si mesmo não cruza camada nenhuma.
            // Sem isto, uma regra com `from` e `to` na MESMA camada (o caso de `must-route-through`
            // com origem e destino coincidentes, e de qualquer política que proíba acoplamento
            // interno) acusaria todo método privado que uma classe chama de si mesma.
            if (IsSameType(sourceType, targetType))
            {
                return;
            }

            if (!layerResolver.IsInLayer(sourceType, fromLayer))
            {
                return;
            }

            // Controle de falso positivo nº 2, propositalmente o ÚLTIMO teste: `nameof(Repo.Prop)`
            // produz um IPropertyReferenceOperation de verdade, mas não executa acesso nenhum — é
            // uma constante de compilação. Como este teste só roda sobre candidatos que já passaram
            // pelas duas checagens de camada (ou seja, sobre violações prestes a ser reportadas),
            // andar na cadeia de pais aqui não tem custo mensurável no caso comum.
            if (IsInsideNameOf(operation))
            {
                return;
            }

            operationContext.ReportDiagnostic(Diagnostic.Create(
                slotDescriptor,
                operation.Syntax.GetLocation(),
                properties,
                BuildMessage(rule, template, fromLayer, toLayer, symbolText)));
        }

        /// <summary>
        /// Como o `message` da regra (YAML) e o <c>MessageFormat</c> do slot se combinam — decisão de
        /// design, não coberta em detalhe pelo ADR.
        ///
        /// <para><b>Fato de contrato:</b> todo descriptor do pool nasce com
        /// <c>messageFormat: "{0}"</c> (ver <c>ArchDiagnostics.PassThroughMessageFormat</c>) — UM
        /// único argumento, que é a mensagem final já composta. O descriptor é construído antes de o
        /// YAML existir, então ele não pode conhecer o texto da política; passar mais de um argumento
        /// a <c>Diagnostic.Create</c> aqui é erro silencioso (os extras seriam ignorados), e passar
        /// zero produziria a string literal "{0}" na Error List.</para>
        ///
        /// <para><b>Decisão:</b> o slot recebe exatamente uma string, montada como
        /// <c>"&lt;id de negócio&gt;: &lt;template expandido&gt;"</c>.</para>
        /// <list type="number">
        /// <item>O prefixo com <see cref="RuleDefinition.Id"/> é SEMPRE adicionado pelo motor, e não
        /// deixado a cargo do template do consumidor: o que o compilador, o `#pragma` e a Error List
        /// mostram é o slot técnico (ARCH0007), que não diz nada sobre a política. Sem o prefixo, o
        /// ID de negócio (ACME-001) poderia sumir da mensagem só porque alguém escreveu um `message:`
        /// sem ele — e a rastreabilidade entre diagnóstico e regra do YAML é o ponto inteiro do
        /// esquema de dois níveis de identidade (ADR-001 §3, Opção C). O mesmo prefixo é usado pelo
        /// avaliador de teste que provou o pipeline na Fase 1B, então o formato já é o esperado.</item>
        /// <item>A expansão de placeholders é feita à mão, NUNCA com <c>string.Format</c>: o template
        /// é texto arbitrário do consumidor e um `{` solto (ou um `{0}`) faria
        /// <c>string.Format</c> lançar <see cref="FormatException"/> dentro do analyzer — que vira
        /// AD0001 e derruba a análise inteira daquele projeto por causa de uma chave mal digitada no
        /// YAML. Placeholder desconhecido é copiado literalmente, nunca é erro.</item>
        /// </list>
        /// </summary>
        protected virtual string BuildMessage(
            RuleDefinition rule,
            string template,
            string fromLayer,
            string toLayer,
            string symbolText)
        {
            return rule.Id + ": " + ExpandTemplate(template, rule, fromLayer, toLayer, symbolText);
        }

        /// <summary>
        /// Substitui `{from}`, `{to}`, `{symbol}` e `{id}` — mais QUALQUER chave presente em
        /// <see cref="RuleDefinition.Extra"/>, o que faz `{through}` funcionar de graça para
        /// `must-route-through` (trilha 2B) sem um hook próprio. Um `{nome}` não resolvido fica no
        /// texto como está; um `{` sem `}` correspondente encerra a expansão e o resto é copiado
        /// literal. Nenhum caminho lança exceção.
        /// </summary>
        protected static string ExpandTemplate(
            string template,
            RuleDefinition rule,
            string fromLayer,
            string toLayer,
            string symbolText)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0)
            {
                return template ?? string.Empty;
            }

            var builder = new StringBuilder(template.Length + 32);
            var index = 0;

            while (index < template.Length)
            {
                var open = template.IndexOf('{', index);
                if (open < 0)
                {
                    builder.Append(template, index, template.Length - index);
                    break;
                }

                var close = template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    builder.Append(template, index, template.Length - index);
                    break;
                }

                builder.Append(template, index, open - index);

                var name = template.Substring(open + 1, close - open - 1);
                string value;
                if (TryResolvePlaceholder(name, rule, fromLayer, toLayer, symbolText, out value))
                {
                    builder.Append(value);
                }
                else
                {
                    builder.Append(template, open, close - open + 1);
                }

                index = close + 1;
            }

            return builder.ToString();
        }

        private static bool TryResolvePlaceholder(
            string name,
            RuleDefinition rule,
            string fromLayer,
            string toLayer,
            string symbolText,
            out string value)
        {
            value = null;

            switch (name)
            {
                case FromKey:
                    value = fromLayer;
                    return true;
                case ToKey:
                    value = toLayer;
                    return true;
                case "symbol":
                    value = symbolText;
                    return true;
                case "id":
                    value = rule.Id;
                    return true;
            }

            // Demais campos do YAML da própria regra (`through`, `layer`, `max`, `countBy`, ...).
            // Extra é Ordinal (parser da trilha 1A), então a grafia do placeholder é sensível a caixa
            // exatamente como a chave do YAML — o que é o comportamento desejável: `{countBy}` é a
            // chave, `{countby}` não existe.
            return rule.Extra != null && rule.Extra.TryGetValue(name, out value);
        }

        /// <summary>
        /// Tipo que DECLARA o membro alcançado pela operação, e um texto curto e determinístico para
        /// `{symbol}`. Declarante, não receptor: uma chamada via interface `IOrderRepository` deve ser
        /// classificada pela interface (que é o que a política enxerga como camada), e uma chamada a
        /// método de extensão pela classe estática que a declara.
        /// </summary>
        private static bool TryGetTarget(IOperation operation, out INamedTypeSymbol targetType, out string symbolText)
        {
            targetType = null;
            symbolText = null;

            var invocation = operation as IInvocationOperation;
            if (invocation != null)
            {
                var method = invocation.TargetMethod;
                if (method == null)
                {
                    return false;
                }

                targetType = method.ContainingType;
                symbolText = Describe(targetType, method.Name);
                return targetType != null;
            }

            var creation = operation as IObjectCreationOperation;
            if (creation != null)
            {
                // Constructor é null em cenários legítimos (`new T()` sobre parâmetro de tipo com
                // restrição `new()`) e em código com erro; o tipo construído ainda é a informação
                // que a regra precisa.
                var constructor = creation.Constructor;
                targetType = constructor != null
                    ? constructor.ContainingType
                    : creation.Type as INamedTypeSymbol;

                if (targetType == null)
                {
                    return false;
                }

                symbolText = "new " + targetType.Name;
                return true;
            }

            var propertyReference = operation as IPropertyReferenceOperation;
            if (propertyReference != null)
            {
                var property = propertyReference.Property;
                if (property == null)
                {
                    return false;
                }

                targetType = property.ContainingType;
                symbolText = Describe(targetType, property.Name);
                return targetType != null;
            }

            var fieldReference = operation as IFieldReferenceOperation;
            if (fieldReference != null)
            {
                var field = fieldReference.Field;
                if (field == null)
                {
                    return false;
                }

                targetType = field.ContainingType;
                symbolText = Describe(targetType, field.Name);
                return targetType != null;
            }

            return false;
        }

        private static string Describe(INamedTypeSymbol containingType, string memberName)
        {
            return containingType == null ? memberName : containingType.Name + "." + memberName;
        }

        /// <summary>
        /// O tipo que CONTÉM o código que executa a operação.
        ///
        /// <para><c>OperationAnalysisContext.ContainingSymbol</c> não é necessariamente um método de
        /// um tipo: dentro de uma função local ele é o <see cref="IMethodSymbol"/> da própria função
        /// local (cujo <c>ContainingSymbol</c> é o método que a declara, e só então vem o tipo), e a
        /// mesma cadeia aparece com funções locais aninhadas. Por isso a subida é um laço, e não um
        /// único <c>.ContainingType</c> — que, no caso da função local, devolveria o tipo certo por
        /// acidente hoje e o errado no dia em que o Roslyn mudar o símbolo reportado. O laço termina
        /// sempre: acima de namespace está o assembly, cujo <c>ContainingSymbol</c> é null.</para>
        ///
        /// <para>Lambdas não criam símbolo próprio de contenção (a operação pertence ao método que a
        /// declara), então caem no mesmo caminho sem tratamento especial. O tipo devolvido é o MAIS
        /// INTERNO: numa classe aninhada, quem faz a chamada é a aninhada, não a externa.</para>
        /// </summary>
        private static INamedTypeSymbol GetContainingType(ISymbol symbol)
        {
            for (var current = symbol; current != null; current = current.ContainingSymbol)
            {
                var named = current as INamedTypeSymbol;
                if (named != null)
                {
                    return named;
                }
            }

            return null;
        }

        private static bool IsSameType(INamedTypeSymbol source, INamedTypeSymbol target)
        {
            // OriginalDefinition: `Cache<int>` chamando um membro de `Cache<T>` é a mesma classe
            // falando consigo mesma, e não um cruzamento de camadas.
            return SymbolEqualityComparer.Default.Equals(source.OriginalDefinition, target.OriginalDefinition);
        }

        private static bool IsInsideNameOf(IOperation operation)
        {
            for (var current = operation.Parent; current != null; current = current.Parent)
            {
                if (current is INameOfOperation)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetLayerName(RuleDefinition rule, string key, out string layerName)
        {
            string raw;
            if (rule.Extra != null && rule.Extra.TryGetValue(key, out raw) && !string.IsNullOrWhiteSpace(raw))
            {
                layerName = raw.Trim();
                return true;
            }

            layerName = null;
            return false;
        }

        private static ImmutableDictionary<string, string> BuildProperties(
            RuleDefinition rule,
            string fromLayer,
            string toLayer)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

            builder[RuleIdProperty] = rule.Id;
            builder[RuleTypeProperty] = string.IsNullOrEmpty(rule.Type) ? TypeName : rule.Type;
            builder[FromProperty] = fromLayer;
            builder[ToProperty] = toLayer;

            if (!string.IsNullOrEmpty(rule.Help))
            {
                builder[HelpProperty] = rule.Help;
            }

            return builder.ToImmutable();
        }
    }
}
