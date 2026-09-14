using System.Runtime.CompilerServices;

// Permite que Arch.Config.Tests exercite diretamente o overload interno
// ArchConfigParser.Parse(IReadOnlyList<string>) usado para testar a semântica de `extends`
// (ADR-001 D2) sem depender de I/O real de arquivo — ver comentário na assinatura pública
// congelada ArchConfigParser.Parse(string) em ArchConfigParser.cs.
[assembly: InternalsVisibleTo("Arch.Config.Tests")]
