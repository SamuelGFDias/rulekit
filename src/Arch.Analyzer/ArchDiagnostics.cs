using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Arch.Analyzer
{
    /// <summary>
    /// Pool de slots pré-declarados (ADR-001 §3, Opção C) + faixa reservada do motor (ADR-001 D4).
    ///
    /// Por que um pool fixo: <see cref="Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer.SupportedDiagnostics"/>
    /// é lida pelo Roslyn SEM contexto de compilação (antes de os AdditionalFiles/YAML existirem) e
    /// fica cacheada por assembly do analyzer durante toda a sessão da IDE. Um descriptor construído
    /// em runtime a partir do YAML nunca chega a reportar: vira <c>AD0001</c> silencioso (ADR-001 §1).
    /// Por isso a lib declara <c>ARCH0001..ARCH0512</c> ANTES de saber qualquer coisa sobre a política
    /// do consumidor, e o YAML apenas MAPEIA o ID de negócio (ex.: `ACME-001`) a um desses slots.
    ///
    /// Os 512 descriptors são gerados programaticamente (objetos baratos, uma única vez no static
    /// constructor) — escrever 512 literais à mão seria ruído puro.
    ///
    /// Severidade: todo slot nasce <see cref="DiagnosticSeverity.Warning"/>. Essa é apenas a
    /// severidade DEFAULT do slot; a severidade EFETIVA vem do `.editorconfig`/`.globalconfig`
    /// (`dotnet_diagnostic.ARCH0007.severity = ...`), gerado a partir do bloco `severities:` do YAML
    /// pelo `arch-rules gen` (ADR-001 D3). O descriptor nunca decide política.
    ///
    /// Faixa <c>ARCH9xxx</c>: reservada para erros do próprio motor, sempre habilitada por padrão e
    /// nunca alocável por configuração do consumidor. Deliberadamente SEM
    /// <see cref="WellKnownDiagnosticTags.NotConfigurable"/>: essa tag desligaria
    /// `#pragma warning disable` e `.editorconfig` para o ID, e o time precisa conseguir silenciar
    /// um aviso do motor pontualmente (ADR-001 D4).
    /// </summary>
    internal static class ArchDiagnostics
    {
        internal const string Category = "Architecture";

        /// <summary>Tamanho do pool (ADR-001 §3: "512 cobre qualquer política realista").</summary>
        internal const int SlotCount = 512;

        private const string SlotPrefix = "ARCH";

        /// <summary>
        /// Formato de mensagem de TODO descriptor desta classe. É sempre um único argumento: a
        /// mensagem final já composta (pelo <see cref="Contracts.IRuleEvaluator"/>, a partir do
        /// template `message` da regra no YAML, ou pelo próprio motor para a faixa ARCH9xxx). O
        /// descriptor não pode carregar o texto da regra — ele existe antes de o YAML ser lido.
        /// </summary>
        private const string PassThroughMessageFormat = "{0}";

        /// <summary>Slots do pool na ordem ARCH0001..ARCH0512 (índice 0 = ARCH0001).</summary>
        private static readonly ImmutableArray<DiagnosticDescriptor> Slots;

        private static readonly ImmutableDictionary<string, DiagnosticDescriptor> SlotsById;

        private static readonly ImmutableDictionary<string, DiagnosticDescriptor> EngineById;

        /// <summary>ARCH9001 — configuração inválida (schema/YAML). Invalida o arquivo inteiro.</summary>
        internal static readonly DiagnosticDescriptor Arch9001;

        /// <summary>ARCH9002 — slot inválido (fora do pool) ou duplicado entre regras. Regra ignorada.</summary>
        internal static readonly DiagnosticDescriptor Arch9002;

        /// <summary>ARCH9003 — tipo de regra desconhecido nesta versão da lib. Regra ignorada (MINOR aditivo, ADR-001 D4).</summary>
        internal static readonly DiagnosticDescriptor Arch9003;

        /// <summary>ARCH9004 — nome de tipo (`implements`/`baseType`/`attribute`) não resolvido na compilação. Critério ignorado.</summary>
        internal static readonly DiagnosticDescriptor Arch9004;

        /// <summary>
        /// ARCH9005 — divergência entre a severidade declarada no YAML e a efetiva no
        /// `.globalconfig` (ADR-001 D3). Declarado desde já para que o ID exista em
        /// <see cref="Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer.SupportedDiagnostics"/>
        /// quando a Fase 3 (CLI gerador) passar a reportá-lo — acrescentar um ID depois é barato,
        /// mas reportar um ID não declarado nunca funciona (AD0001).
        /// </summary>
        internal static readonly DiagnosticDescriptor Arch9005;

        /// <summary>
        /// Tudo que o analyzer pode reportar: os 512 slots + a faixa ARCH9xxx. É exatamente o que
        /// <see cref="Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer.SupportedDiagnostics"/>
        /// devolve — imutável e computada uma única vez.
        /// </summary>
        internal static readonly ImmutableArray<DiagnosticDescriptor> All;

        static ArchDiagnostics()
        {
            var slots = ImmutableArray.CreateBuilder<DiagnosticDescriptor>(SlotCount);
            var slotsById = ImmutableDictionary.CreateBuilder<string, DiagnosticDescriptor>(StringComparer.OrdinalIgnoreCase);

            for (var number = 1; number <= SlotCount; number++)
            {
                var id = SlotPrefix + number.ToString("D4", CultureInfo.InvariantCulture);
                var descriptor = new DiagnosticDescriptor(
                    id: id,
                    title: "Violação de regra arquitetural (slot " + id + ")",
                    messageFormat: PassThroughMessageFormat,
                    category: Category,
                    defaultSeverity: DiagnosticSeverity.Warning,
                    isEnabledByDefault: true,
                    description:
                        "Slot técnico do pool de diagnósticos (ADR-001 §3, Opção C). O vínculo entre este slot e uma " +
                        "regra concreta é declarado em arch-rules.yaml (`slot: " + id + "`); o ID de negócio da " +
                        "organização aparece na mensagem e em Diagnostic.Properties. A severidade efetiva vem do " +
                        ".editorconfig/.globalconfig, nunca deste descriptor. Slot sem regra vinculada nunca reporta nada.");

                slots.Add(descriptor);
                slotsById.Add(id, descriptor);
            }

            Slots = slots.MoveToImmutable();
            SlotsById = slotsById.ToImmutable();

            Arch9001 = CreateEngineDescriptor(
                id: "ARCH9001",
                title: "Configuração arquitetural inválida",
                defaultSeverity: DiagnosticSeverity.Error,
                description:
                    "O arch-rules.yaml não pôde ser aceito (schema major não suportada, YAML malformado ou violação de " +
                    "schema). Nenhuma regra é avaliada enquanto o arquivo estiver inválido — falha ruidosa, nunca " +
                    "silenciosa (ADR-001 D4).");

            Arch9002 = CreateEngineDescriptor(
                id: "ARCH9002",
                title: "Slot inválido ou duplicado em arch-rules.yaml",
                defaultSeverity: DiagnosticSeverity.Warning,
                description:
                    "A regra referencia um slot fora do pool ARCH0001..ARCH0512, ou reutiliza um slot já vinculado a " +
                    "outra regra. A regra é ignorada; as demais continuam valendo. Dois IDs de negócio no mesmo slot " +
                    "tornariam impossível dar override de severidade a um sem afetar o outro (ADR-001 §3, Opção C).");

            Arch9003 = CreateEngineDescriptor(
                id: "ARCH9003",
                title: "Tipo de regra desconhecido",
                defaultSeverity: DiagnosticSeverity.Warning,
                description:
                    "O `type` da regra não é suportado por esta versão da lib. Política MINOR-aditiva (ADR-001 D4): a " +
                    "regra é ignorada e as demais continuam valendo — atualizar a lib costuma resolver.");

            Arch9004 = CreateEngineDescriptor(
                id: "ARCH9004",
                title: "Tipo referenciado na configuração não foi resolvido",
                defaultSeverity: DiagnosticSeverity.Warning,
                description:
                    "Um nome usado em `implements`/`baseType`/`attribute` não existe nas referências desta compilação " +
                    "(Compilation.GetTypeByMetadataName devolveu null). O critério é ignorado — causas usuais: nome " +
                    "não totalmente qualificado, arity de genérico faltando (`Ns.IFoo`1`) ou pacote não referenciado " +
                    "por este projeto. Reportado uma vez por nome, nunca por símbolo (ADR-001 §5).");

            Arch9005 = CreateEngineDescriptor(
                id: "ARCH9005",
                title: "Severidade do YAML diverge do .globalconfig",
                defaultSeverity: DiagnosticSeverity.Info,
                description:
                    "A severidade declarada no arch-rules.yaml não corresponde à severidade efetiva do slot no " +
                    ".editorconfig/.globalconfig — sinal de que `arch-rules gen` não foi reexecutado (ADR-001 D3). " +
                    "Reservado: reportado a partir da Fase 3.");

            EngineById = ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                new[]
                {
                    new KeyValuePair<string, DiagnosticDescriptor>(Arch9001.Id, Arch9001),
                    new KeyValuePair<string, DiagnosticDescriptor>(Arch9002.Id, Arch9002),
                    new KeyValuePair<string, DiagnosticDescriptor>(Arch9003.Id, Arch9003),
                    new KeyValuePair<string, DiagnosticDescriptor>(Arch9004.Id, Arch9004),
                    new KeyValuePair<string, DiagnosticDescriptor>(Arch9005.Id, Arch9005),
                });

            // Ordem explícita (não a de EngineById, que é um dicionário) para que SupportedDiagnostics
            // seja estável entre execuções — ferramentas de documentação e testes comparam essa lista.
            All = Slots.Add(Arch9001).Add(Arch9002).Add(Arch9003).Add(Arch9004).Add(Arch9005);
        }

        private static DiagnosticDescriptor CreateEngineDescriptor(
            string id,
            string title,
            DiagnosticSeverity defaultSeverity,
            string description)
        {
            // Sem WellKnownDiagnosticTags.NotConfigurable: a tag bloquearia #pragma e .editorconfig
            // para a faixa do motor, e silenciar um aviso de motor pontualmente é requisito (ADR-001 D4).
            return new DiagnosticDescriptor(
                id: id,
                title: title,
                messageFormat: PassThroughMessageFormat,
                category: Category,
                defaultSeverity: defaultSeverity,
                isEnabledByDefault: true,
                description: description);
        }

        /// <summary>
        /// Descriptor pré-declarado do slot técnico (`rules[].slot` no YAML), ou false se o valor
        /// estiver fora do pool. NUNCA construa um DiagnosticDescriptor em runtime a partir do YAML:
        /// o ID não estaria em SupportedDiagnostics e o diagnóstico viraria AD0001 (ADR-001 §1).
        /// </summary>
        internal static bool TryGetSlot(string slotId, out DiagnosticDescriptor descriptor)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                descriptor = null;
                return false;
            }

            return SlotsById.TryGetValue(slotId.Trim(), out descriptor);
        }

        /// <summary>
        /// Descriptor da faixa reservada do motor correspondente ao <paramref name="code"/> que o
        /// parser reportou (<c>SchemaValidationError.Code</c>). Um código desconhecido (ex.: uma
        /// versão futura de Arch.Config reportando ARCH9006) não pode ser reportado como ele mesmo —
        /// não está em SupportedDiagnostics — então cai para ARCH9001 quando bloqueia o arquivo e
        /// ARCH9002 quando não bloqueia, com o código original preservado na mensagem por
        /// <c>ArchRulesAnalyzer</c>.
        /// </summary>
        internal static DiagnosticDescriptor GetEngineDescriptorOrFallback(string code, bool isBlocking)
        {
            if (!string.IsNullOrWhiteSpace(code) && EngineById.TryGetValue(code.Trim(), out var descriptor))
            {
                return descriptor;
            }

            return isBlocking ? Arch9001 : Arch9002;
        }

        /// <summary>true se <paramref name="code"/> é um ID declarado da faixa reservada do motor.</summary>
        internal static bool IsKnownEngineCode(string code)
        {
            return !string.IsNullOrWhiteSpace(code) && EngineById.ContainsKey(code.Trim());
        }
    }
}
