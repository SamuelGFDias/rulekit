using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Arch.Analyzer
{
    /// <summary>
    /// Pool de slots do spike (ADR-001 §3, Opção C). Em produção a lib pré-declara
    /// ARCH0001..ARCH0512; este spike declara só ARCH0001..ARCH0003 para provar o mecanismo
    /// de ponta a ponta sem custo de escrever 512 descriptors à mão. ARCH0001 é o único com
    /// regra vinculada neste spike (sufixo proibido lido do YAML); ARCH0002/ARCH0003 existem
    /// só para provar que o pool tem mais de um slot pré-declarado e nenhum deles quebra
    /// SupportedDiagnostics.
    /// </summary>
    internal static class ArchDiagnostics
    {
        internal const string Category = "Architecture";

        internal static readonly DiagnosticDescriptor Arch0001 = new DiagnosticDescriptor(
            id: "ARCH0001",
            title: "Invocação de tipo com sufixo proibido pela política arquitetural",
            messageFormat: "'{0}' termina com o sufixo '{1}', proibido pela política arquitetural configurada em arch-rules.yaml",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Slot técnico ARCH0001 do pool de slots (ADR-001 D2/§3 Opção C). O ID de negócio da organização e a severidade efetiva vêm do arch-rules.yaml e do .editorconfig/.globalconfig, nunca deste descriptor.",
            helpLinkUri: "https://example.invalid/arch/ARCH0001");

        internal static readonly DiagnosticDescriptor Arch0002 = new DiagnosticDescriptor(
            id: "ARCH0002",
            title: "Slot reservado do pool (spike)",
            messageFormat: "Slot ARCH0002 reservado — sem regra vinculada neste spike de viabilidade",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Prova que o pool de slots pré-declara mais de um ID e todos convivem em SupportedDiagnostics mesmo sem regra vinculada.");

        internal static readonly DiagnosticDescriptor Arch0003 = new DiagnosticDescriptor(
            id: "ARCH0003",
            title: "Slot reservado do pool (spike)",
            messageFormat: "Slot ARCH0003 reservado — sem regra vinculada neste spike de viabilidade",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Prova que o pool de slots pré-declara mais de um ID e todos convivem em SupportedDiagnostics mesmo sem regra vinculada.");

        internal static readonly ImmutableArray<DiagnosticDescriptor> All =
            ImmutableArray.Create(Arch0001, Arch0002, Arch0003);
    }
}
