using System.Runtime.CompilerServices;

// O ponto de extensão do motor (registro de IRuleEvaluator) e a fronteira com o parser de
// Arch.Config são internal de propósito: não são superfície pública da lib, e um consumidor não tem
// por que construir o analyzer à mão. O projeto de testes precisa dos dois para provar o pipeline
// ponta a ponta (registrar um avaliador de teste e injetar uma ArchConfig determinística sem
// depender da trilha 1A) — daí o InternalsVisibleTo, que é o mecanismo padrão para isso e não
// aumenta a API pública do pacote.
[assembly: InternalsVisibleTo("Arch.Analyzer.Tests")]

// Benchmarks de memoização do LayerResolver (tests/Arch.Benchmarks) chamam LayerResolver.Create
// diretamente. Antes isso era feito por reflexão + Delegate.CreateDelegate, um contorno para tipos
// duplicados entre o Arch.Analyzer.dll fundido e as DLLs soltas — que deixou de existir quando o
// ILRepack passou a rodar só no `dotnet pack` (issue #11, item 3).
[assembly: InternalsVisibleTo("Arch.Benchmarks")]
