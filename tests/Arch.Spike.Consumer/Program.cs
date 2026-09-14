namespace Arch.Spike.Consumer
{
    /// <summary>Tipo cujo nome termina no sufixo declarado em arch-rules.yaml ("Repository").</summary>
    public class FooRepository
    {
        public void Do()
        {
        }
    }

    /// <summary>
    /// Prova, num consumidor real (via .nupkg local, não ProjectReference), os 3 itens do
    /// gate da Fase 0 sobre o mecanismo de pool de slots (ADR-001 §3 Opção C):
    ///
    ///  (a) a chamada NÃO suprimida abaixo produz ARCH0001 no `dotnet build` de verdade;
    ///  (b) com dotnet_diagnostic.ARCH0001.severity = error no .editorconfig deste projeto,
    ///      esse mesmo ARCH0001 vira erro e quebra o build (ver .editorconfig ao lado);
    ///  (c) a segunda chamada, idêntica, mas envolvida em
    ///      #pragma warning disable ARCH0001, é suprimida — não aparece no log de build,
    ///      mesmo com o slot ativo e a regra casando.
    /// </summary>
    public class Consumer
    {
        public void UsesForbiddenType()
        {
            new FooRepository().Do(); // ARCH0001 esperado aqui (não suprimido)

#pragma warning disable ARCH0001
            new FooRepository().Do(); // suprimido por #pragma — não deve gerar diagnóstico
#pragma warning restore ARCH0001
        }
    }
}
