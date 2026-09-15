using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Diagnóstico unificado para impressão no console pelos comandos `gen`/`validate`, cobrindo
    /// tanto os erros de schema de <see cref="SchemaValidationError"/> (linha/coluna reais,
    /// reportados pelo parser puro de Arch.Config) quanto as validações adicionais que este CLI
    /// faz por conta própria — slot fora do pool/duplicado (<see cref="SlotValidator"/>) e
    /// severidade inválida (<see cref="SeverityResolver"/>), nenhuma das quais o parser de
    /// Arch.Config verifica (ver XML doc de cada validador). Nesses casos Line/Column ficam 0/0,
    /// seguindo a mesma convenção de "posição desconhecida" já usada por
    /// <see cref="SchemaValidationError"/> para erros sem um nó YAML útil para apontar.
    /// </summary>
    public sealed class CliDiagnostic
    {
        public string Code { get; }
        public string Message { get; }
        public int Line { get; }
        public int Column { get; }

        public CliDiagnostic(string code, string message, int line, int column)
        {
            Code = code;
            Message = message;
            Line = line;
            Column = column;
        }

        public static CliDiagnostic FromSchemaError(SchemaValidationError error)
        {
            return new CliDiagnostic(error.Code, error.Message, error.Line, error.Column);
        }

        public override string ToString()
        {
            return $"[{Code}] linha {Line}, coluna {Column}: {Message}";
        }
    }
}
