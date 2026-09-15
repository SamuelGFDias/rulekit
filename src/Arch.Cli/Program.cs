using System;
using System.IO;

namespace Arch.Cli
{
    /// <summary>
    /// Ponto de entrada do dotnet tool `arch-rules` (Fase 3 / PLANO-implementacao.md, trilha
    /// "cli-generator"). Parsing de argumentos feito à mão (ver comentário em Arch.Cli.csproj) —
    /// dois subcomandos, `gen` e `validate`, cada um com um argumento posicional e no máximo uma
    /// opção. O trabalho de verdade fica em <see cref="GenCommand"/>/<see cref="ValidateCommand"/>,
    /// que recebem um <see cref="TextWriter"/> em vez de escrever direto em
    /// <see cref="Console"/> — isso é o que permite <c>Arch.Cli.Tests</c> capturar a saída sem
    /// precisar rodar o processo `arch-rules` de verdade.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            return Run(args, Console.Out, Console.Error);
        }

        public static int Run(string[] args, TextWriter output, TextWriter error)
        {
            if (args.Length == 0)
            {
                PrintUsage(error);
                return 1;
            }

            switch (args[0])
            {
                case "gen":
                    return RunGen(args, output, error);
                case "validate":
                    return RunValidate(args, output, error);
                case "-h":
                case "--help":
                    PrintUsage(output);
                    return 0;
                default:
                    error.WriteLine($"Comando desconhecido: '{args[0]}'.");
                    PrintUsage(error);
                    return 1;
            }
        }

        private static int RunGen(string[] args, TextWriter output, TextWriter error)
        {
            string yamlPath = null;
            string outDir = null;

            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--out")
                {
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("--out exige um valor (diretório de saída).");
                        return 1;
                    }

                    outDir = args[++i];
                }
                else if (yamlPath == null)
                {
                    yamlPath = args[i];
                }
                else
                {
                    error.WriteLine($"Argumento inesperado: '{args[i]}'.");
                    return 1;
                }
            }

            if (yamlPath == null)
            {
                error.WriteLine("Uso: arch-rules gen <caminho-do-yaml> [--out <diretorio>]");
                return 1;
            }

            return GenCommand.Run(yamlPath, outDir, output);
        }

        private static int RunValidate(string[] args, TextWriter output, TextWriter error)
        {
            if (args.Length < 2)
            {
                error.WriteLine("Uso: arch-rules validate <caminho-do-yaml>");
                return 1;
            }

            return ValidateCommand.Run(args[1], output);
        }

        private static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("Uso:");
            writer.WriteLine("  arch-rules gen <caminho-do-yaml> [--out <diretorio>]");
            writer.WriteLine("  arch-rules validate <caminho-do-yaml>");
        }
    }
}
