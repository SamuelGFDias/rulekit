using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>Uma violação de slot lida do SARIF (issue #25): regra de negócio, assunto estável e fonte absoluta.</summary>
    internal sealed class SarifViolation
    {
        internal SarifViolation(string rule, string subject, string sourceFile)
        {
            Rule = rule;
            Subject = subject;
            SourceFile = sourceFile;
        }

        internal string Rule { get; }

        internal string Subject { get; }

        internal string SourceFile { get; }
    }

    /// <summary>
    /// Leitor de SARIF 2.1 (<c>-p:ErrorLog=build.sarif,version=2.1</c>) com <c>System.Text.Json</c>. Só
    /// interessam resultados cujo <c>ruleId</c> é um slot (<see cref="SlotPool"/>), suprimidos ou não. O
    /// Roslyn grava <c>Diagnostic.Properties</c> em <c>properties.customProperties</c>; <c>properties</c>
    /// direto também é aceito (tolerância a outros emissores). O arquivo vem de
    /// <c>locations[0].physicalLocation.artifactLocation.uri</c> — <c>file:///</c> absoluta, ou relativa a
    /// <c>originalUriBaseIds[uriBaseId]</c>.
    /// </summary>
    internal static class SarifReader
    {
        internal const string RuleIdKey = "archRuleId";
        internal const string SubjectKey = "archSubject";

        /// <summary>Lê um SARIF; erro de leitura/forma devolve <c>false</c> com a mensagem em <paramref name="error"/>.</summary>
        internal static bool TryRead(string path, List<SarifViolation> violations, List<string> warnings, out string error)
        {
            error = null;
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = $"Não foi possível ler o SARIF '{path}': {ex.Message}";
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                error = $"'{path}' não é um JSON válido: {ex.Message}";
                return false;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("runs", out var runs)
                    || runs.ValueKind != JsonValueKind.Array)
                {
                    error = $"'{path}' não parece um SARIF 2.1 (sem a lista 'runs').";
                    return false;
                }

                foreach (var run in runs.EnumerateArray())
                {
                    ReadRun(run, path, violations, warnings);
                }
            }

            return true;
        }

        private static void ReadRun(JsonElement run, string sarifPath, List<SarifViolation> violations, List<string> warnings)
        {
            if (run.ValueKind != JsonValueKind.Object
                || !run.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var baseIds = ReadBaseIds(run);

            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object
                    || !(GetString(result, "ruleId") is string slotId)
                    || !SlotPool.TryParse(slotId, out _))
                {
                    continue;
                }

                string uri = null;
                string uriBaseId = null;
                if (result.TryGetProperty("locations", out var locations)
                    && locations.ValueKind == JsonValueKind.Array
                    && locations.GetArrayLength() > 0
                    && locations[0].TryGetProperty("physicalLocation", out var physical)
                    && physical.ValueKind == JsonValueKind.Object
                    && physical.TryGetProperty("artifactLocation", out var artifact)
                    && artifact.ValueKind == JsonValueKind.Object)
                {
                    uri = GetString(artifact, "uri");
                    uriBaseId = GetString(artifact, "uriBaseId");
                }

                var (rule, subject) = ReadProperties(result);
                if (rule == null || subject == null)
                {
                    warnings.Add(
                        $"Resultado {slotId} em '{uri ?? "?"}' sem archRuleId/archSubject (analyzer anterior ao baseline?): ignorado.");
                    continue;
                }

                if (uri == null || !TryResolveSource(uri, uriBaseId, baseIds, out var source))
                {
                    warnings.Add($"Resultado {slotId} ({rule}) sem arquivo-fonte resolvível (uri '{uri ?? "?"}'): ignorado.");
                    continue;
                }

                violations.Add(new SarifViolation(rule, subject, source));
            }
        }

        private static (string Rule, string Subject) ReadProperties(JsonElement result)
        {
            if (!result.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var holder = properties.TryGetProperty("customProperties", out var custom) && custom.ValueKind == JsonValueKind.Object
                ? custom
                : properties;

            var rule = GetString(holder, RuleIdKey);
            var subject = GetString(holder, SubjectKey);
            return string.IsNullOrEmpty(rule) || subject == null ? (null, null) : (rule, subject);
        }

        private static Dictionary<string, string> ReadBaseIds(JsonElement run)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (run.TryGetProperty("originalUriBaseIds", out var ids) && ids.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in ids.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object && GetString(property.Value, "uri") is string uri)
                    {
                        map[property.Name] = uri;
                    }
                }
            }

            return map;
        }

        private static string GetString(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        /// <summary>URI absoluta de arquivo, ou relativa a <c>originalUriBaseIds[uriBaseId]</c> (ou ao diretório atual).</summary>
        internal static bool TryResolveSource(string uri, string uriBaseId, Dictionary<string, string> baseIds, out string path)
        {
            path = null;
            if (System.Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
            {
                if (!absolute.IsFile)
                {
                    return false;
                }

                path = absolute.LocalPath;
                return true;
            }

            var relative = System.Uri.UnescapeDataString(uri);
            if (uriBaseId != null && baseIds.TryGetValue(uriBaseId, out var baseUri))
            {
                if (!System.Uri.TryCreate(baseUri, UriKind.Absolute, out var parsedBase) || !parsedBase.IsFile)
                {
                    return false;
                }

                path = Path.GetFullPath(Path.Combine(parsedBase.LocalPath, relative));
                return true;
            }

            path = Path.GetFullPath(relative);
            return true;
        }
    }
}
