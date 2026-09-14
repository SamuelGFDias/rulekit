using System.Runtime.CompilerServices;

// O ponto de extensão do motor (registro de IRuleEvaluator) e a fronteira com o parser de
// Arch.Config são internal de propósito: não são superfície pública da lib, e um consumidor não tem
// por que construir o analyzer à mão. O projeto de testes precisa dos dois para provar o pipeline
// ponta a ponta (registrar um avaliador de teste e injetar uma ArchConfig determinística sem
// depender da trilha 1A) — daí o InternalsVisibleTo, que é o mecanismo padrão para isso e não
// aumenta a API pública do pacote.
[assembly: InternalsVisibleTo("Arch.Analyzer.Tests")]
