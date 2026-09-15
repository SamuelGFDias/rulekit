using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Arch.Cli
{
    /// <summary>
    /// Erro de resolução de `extends` a partir do disco: ciclo detectado ou arquivo referenciado
    /// inexistente. Não é um <see cref="Arch.Config.SchemaValidationError"/> porque acontece antes
    /// de qualquer texto YAML chegar ao parser (Arch.Config.ArchConfigParser) — é um problema de
    /// I/O/topologia da cadeia de `extends`, responsabilidade de quem chama o parser (ver XML doc
    /// de <c>ArchConfigParser.Parse(string)</c>), não do parser em si.
    /// </summary>
    public sealed class ExtendsResolutionException : Exception
    {
        public ExtendsResolutionException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Resolve a cadeia de `extends` de um `arch-rules.yaml` a partir do disco (ADR-001 D2):
    /// cada `extends` é uma lista de caminhos relativos ao diretório do ARQUIVO QUE OS DECLARA
    /// (não só do arquivo raiz) — um arquivo estendido pode por sua vez estender outro, e a
    /// resolução precisa reancorar o relativo a cada nível. Devolve os textos em ordem CRESCENTE
    /// de precedência (base primeiro, mais específico por último), a mesma ordem exigida por
    /// <see cref="Arch.Config.ArchConfigParser.Parse(IReadOnlyList{string})"/> — "o arquivo que faz
    /// extends vence" (ADR-001 D2).
    ///
    /// Este extrator só entende `extends:` o suficiente para montar a cadeia; não valida o resto
    /// do schema (isso é responsabilidade de <see cref="Arch.Config.ArchConfigParser"/>, chamado
    /// depois com os textos já resolvidos). Um YAML sintaticamente inválido aqui não é reportado
    /// por este tipo — a chamada a ArchConfigParser.Parse subsequente é que produz o
    /// SchemaValidationError com linha/coluna precisas.
    /// </summary>
    public static class ExtendsResolver
    {
        public static IReadOnlyList<string> ResolveOrderedYamlTexts(string yamlPath)
        {
            var chain = new List<string>();
            var stack = new List<string>();
            Resolve(Path.GetFullPath(yamlPath), stack, chain);
            return chain;
        }

        private static void Resolve(string fullPath, List<string> stack, List<string> chain)
        {
            if (stack.Any(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                var cycleDescription = string.Join(" -> ", stack.Select(Path.GetFileName).Append(Path.GetFileName(fullPath)));
                throw new ExtendsResolutionException($"Ciclo de 'extends' detectado: {cycleDescription}.");
            }

            if (!File.Exists(fullPath))
            {
                throw new ExtendsResolutionException($"Arquivo referenciado em 'extends' não encontrado: '{fullPath}'.");
            }

            stack.Add(fullPath);

            var text = File.ReadAllText(fullPath);
            var baseDir = Path.GetDirectoryName(fullPath) ?? ".";

            foreach (var relative in TryExtractExtendsPaths(text))
            {
                var resolved = Path.GetFullPath(Path.Combine(baseDir, relative));
                Resolve(resolved, stack, chain);
            }

            chain.Add(text);
            stack.RemoveAt(stack.Count - 1);
        }

        private static List<string> TryExtractExtendsPaths(string yamlText)
        {
            var result = new List<string>();
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(yamlText ?? string.Empty));

                if (stream.Documents.Count == 0 || !(stream.Documents[0].RootNode is YamlMappingNode root))
                {
                    return result;
                }

                if (!root.Children.TryGetValue(new YamlScalarNode("extends"), out var extendsNode))
                {
                    return result;
                }

                if (extendsNode is YamlSequenceNode sequence)
                {
                    foreach (var item in sequence.Children)
                    {
                        if (item is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
                        {
                            result.Add(scalar.Value);
                        }
                    }
                }
            }
            catch
            {
                // YAML sintaticamente inválido: não é papel deste extrator relatar o erro com
                // linha/coluna — isso é feito por Arch.Config.ArchConfigParser ao processar o
                // texto completo logo em seguida. Aqui basta não travar; a cadeia de extends fica
                // incompleta, mas o próprio texto ainda entra na lista e o Parse final reporta o
                // SchemaValidationError bloqueante de verdade.
            }

            return result;
        }
    }
}
