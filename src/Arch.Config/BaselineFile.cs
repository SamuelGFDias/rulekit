using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Arch.Config.Internal;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Arch.Config
{
    /// <summary>
    /// Uma violação aceita no baseline (issue #25): regra (ID de negócio), arquivo relativo ao diretório do
    /// baseline (sempre com <c>/</c>) e o assunto estável <c>archSubject</c>. Igualdade por valor, com o
    /// caminho já normalizado (<c>\</c> → <c>/</c>, sem <c>./</c> inicial).
    /// </summary>
    public sealed class BaselineEntry : IEquatable<BaselineEntry>
    {
        public string Rule { get; }

        public string File { get; }

        public string Subject { get; }

        public BaselineEntry(string rule, string file, string subject)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            File = BaselineFile.NormalizeFile(file ?? throw new ArgumentNullException(nameof(file)));
            Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        }

        public bool Equals(BaselineEntry other)
            => other != null
                && string.Equals(Rule, other.Rule, StringComparison.Ordinal)
                && string.Equals(File, other.File, StringComparison.Ordinal)
                && string.Equals(Subject, other.Subject, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as BaselineEntry);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Rule);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(File);
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Subject);
            }
        }

        public override string ToString() => Rule + " | " + File + " | " + Subject;
    }

    /// <summary>Resultado de <see cref="BaselineFile.Parse"/>: com erro, <see cref="Entries"/> é vazio.</summary>
    public sealed class BaselineParseResult
    {
        public IReadOnlyList<BaselineEntry> Entries { get; }

        /// <summary>Erros com código <c>ARCH9009</c> e linha/coluna (0/0 quando não há nó a apontar).</summary>
        public IReadOnlyList<SchemaValidationError> Errors { get; }

        public bool IsValid => Errors.Count == 0;

        public BaselineParseResult(IReadOnlyList<BaselineEntry> entries, IReadOnlyList<SchemaValidationError> errors)
        {
            Entries = entries ?? Array.Empty<BaselineEntry>();
            Errors = errors ?? Array.Empty<SchemaValidationError>();
        }
    }

    /// <summary>
    /// Formato do <c>arch-rules.baseline.yaml</c> (issue #25): <c>schema: arch-rules-baseline/v1</c> e
    /// <c>entries:</c> (lista de <c>{ rule, file, subject }</c>). Mesmo subset de YAML do
    /// <c>arch-rules.yaml</c> (<see cref="YamlSubsetGuard"/>); chave desconhecida é erro. Sem Roslyn e sem
    /// I/O; nenhuma exceção escapa de <see cref="Parse"/>.
    /// </summary>
    public static class BaselineFile
    {
        /// <summary>Nome exato do arquivo (o analyzer o procura nos AdditionalFiles por este nome).</summary>
        public const string FileName = "arch-rules.baseline.yaml";

        public const string SchemaValue = "arch-rules-baseline/v1";

        public const string ErrorCode = "ARCH9009";

        private const string EntriesKey = "entries";
        private const string SchemaKey = "schema";

        private static readonly string[] EntryKeys = { "rule", "file", "subject" };

        /// <summary><c>\</c> → <c>/</c> e remoção de <c>./</c> iniciais; é a forma guardada no modelo.</summary>
        public static string NormalizeFile(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            return normalized;
        }

        public static BaselineParseResult Parse(string yamlText)
        {
            try
            {
                return ParseCore(yamlText ?? string.Empty);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return Fail("Falha inesperada ao interpretar o baseline (" + ex.GetType().Name + "): " + ex.Message, 0, 0);
            }
        }

        private static BaselineParseResult ParseCore(string text)
        {
            var subsetError = YamlSubsetGuard.Validate(text);
            if (subsetError != null)
            {
                return Fail(subsetError.Message, subsetError.Line, subsetError.Column);
            }

            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(text));
            }
            catch (YamlException ex)
            {
                return Fail("YAML sintaticamente inválido: " + ex.Message, ex.Start.Line, ex.Start.Column);
            }

            if (stream.Documents.Count == 0 || !(stream.Documents[0].RootNode is YamlMappingNode root))
            {
                return Fail("Documento YAML vazio ou a raiz não é um mapeamento.", 0, 0);
            }

            YamlNode schemaNode = null;
            YamlNode entriesNode = null;
            foreach (var kv in root.Children)
            {
                var key = (kv.Key as YamlScalarNode)?.Value;
                if (key == SchemaKey)
                {
                    schemaNode = kv.Value;
                }
                else if (key == EntriesKey)
                {
                    entriesNode = kv.Value;
                }
                else
                {
                    return Fail("Chave desconhecida no nível raiz do baseline: '" + (key ?? "?") + "'.", kv.Key.Start);
                }
            }

            if (schemaNode == null)
            {
                return Fail("Chave obrigatória 'schema' ausente (esperado '" + SchemaValue + "').", 0, 0);
            }

            if (!(schemaNode is YamlScalarNode schema) || schema.Value != SchemaValue)
            {
                return Fail(
                    "'schema' inválido: esperado '" + SchemaValue + "'"
                        + (schemaNode is YamlScalarNode s ? ", recebido '" + s.Value + "'." : "."),
                    schemaNode.Start);
            }

            if (entriesNode == null)
            {
                return Fail("Chave obrigatória 'entries' ausente (lista de { rule, file, subject }).", 0, 0);
            }

            if (!(entriesNode is YamlSequenceNode sequence))
            {
                return Fail("'entries' deve ser uma lista de { rule, file, subject }.", entriesNode.Start);
            }

            var entries = new List<BaselineEntry>();
            var seen = new HashSet<BaselineEntry>();
            foreach (var item in sequence.Children)
            {
                if (!TryParseEntry(item, out var entry, out var error))
                {
                    return error;
                }

                if (seen.Add(entry))
                {
                    entries.Add(entry);
                }
            }

            return new BaselineParseResult(entries, null);
        }

        private static bool TryParseEntry(YamlNode item, out BaselineEntry entry, out BaselineParseResult error)
        {
            entry = null;
            if (!(item is YamlMappingNode map))
            {
                error = Fail("Cada item de 'entries' deve ser um mapeamento { rule, file, subject }.", item.Start);
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in map.Children)
            {
                var key = (kv.Key as YamlScalarNode)?.Value;
                if (key == null || Array.IndexOf(EntryKeys, key) < 0)
                {
                    error = Fail("Chave desconhecida em item de 'entries': '" + (key ?? "?") + "' (aceitas: rule, file, subject).", item.Start);
                    return false;
                }

                if (!(kv.Value is YamlScalarNode scalar) || string.IsNullOrEmpty(scalar.Value))
                {
                    error = Fail("O campo '" + key + "' de um item de 'entries' deve ser um texto não vazio.", item.Start);
                    return false;
                }

                values[key] = scalar.Value;
            }

            foreach (var required in EntryKeys)
            {
                if (!values.ContainsKey(required))
                {
                    error = Fail("Item de 'entries' sem o campo obrigatório '" + required + "'.", item.Start);
                    return false;
                }
            }

            entry = new BaselineEntry(values["rule"], values["file"], values["subject"]);
            error = null;
            return true;
        }

        private static BaselineParseResult Fail(string message, Mark mark)
            => Fail(message, Math.Max(mark.Line, 1), Math.Max(mark.Column, 1));

        private static BaselineParseResult Fail(string message, int line, int column)
            => new BaselineParseResult(
                null,
                new[] { new SchemaValidationError(ErrorCode, message, line, column, isBlocking: true) });

        /// <summary>
        /// Serializa as entradas de forma determinística: sem duplicatas, ordenadas (ordinal) por arquivo,
        /// regra e assunto; todo texto entre aspas duplas (o parser lê de volta o valor exato). Fim de
        /// linha <c>\n</c>.
        /// </summary>
        public static string Write(IEnumerable<BaselineEntry> entries)
        {
            var sorted = (entries ?? Enumerable.Empty<BaselineEntry>())
                .Where(e => e != null)
                .Distinct()
                .OrderBy(e => e.File, StringComparer.Ordinal)
                .ThenBy(e => e.Rule, StringComparer.Ordinal)
                .ThenBy(e => e.Subject, StringComparer.Ordinal)
                .ToList();

            var builder = new StringBuilder();
            builder.Append("schema: ").Append(SchemaValue).Append('\n');
            if (sorted.Count == 0)
            {
                builder.Append("entries: []\n");
                return builder.ToString();
            }

            builder.Append("entries:\n");
            foreach (var entry in sorted)
            {
                builder.Append("  - rule: ").Append(Quote(entry.Rule)).Append('\n');
                builder.Append("    file: ").Append(Quote(entry.File)).Append('\n');
                builder.Append("    subject: ").Append(Quote(entry.Subject)).Append('\n');
            }

            return builder.ToString();
        }

        private static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == (char)0x85 || c == (char)0x2028 || c == (char)0x2029)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("X4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}
