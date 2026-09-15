using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Arch.Benchmarks.Support
{
    /// <summary>
    /// <see cref="AdditionalText"/> em memória para representar <c>arch-rules.yaml</c> sem tocar em
    /// disco. Cópia deliberada do mesmo padrão usado por
    /// <c>tests/Arch.Analyzer.Tests/TestSupport.cs</c> (InMemoryAdditionalText) — não referenciamos
    /// aquele projeto de teste daqui, e o tipo é trivial o bastante para não valer a pena promovê-lo
    /// a uma dependência compartilhada só para esta trilha (4C, perf-audit).
    ///
    /// O ponto crítico para o benchmark de cache (ADR-001 §5): <see cref="GetText"/> devolve SEMPRE
    /// a mesma INSTÂNCIA de <see cref="SourceText"/> recebida no construtor — é essa identidade de
    /// referência (nunca o caminho, nunca um checksum) que chaveia
    /// <c>ConditionalWeakTable&lt;SourceText, ArchConfig&gt;</c> dentro de <c>ArchRulesAnalyzer</c>.
    /// Duas instâncias diferentes de <see cref="InMemoryAdditionalText"/> com o MESMO texto, mas
    /// SourceText criado separadamente, são deliberadamente tratadas como "arquivos diferentes" pelo
    /// cache — é assim que se força o cenário "frio" no benchmark.
    /// </summary>
    internal sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal InMemoryAdditionalText(string path, SourceText text)
        {
            Path = path;
            _text = text;
        }

        internal InMemoryAdditionalText(string path, string content)
            : this(path, SourceText.From(content))
        {
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
