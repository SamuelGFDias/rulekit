using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Arch.Config;
using Microsoft.CodeAnalysis.Text;

namespace Arch.Analyzer
{
    /// <summary>
    /// Análise do <c>arch-rules.baseline.yaml</c> (issue #25), com cache por INSTÂNCIA de
    /// <see cref="SourceText"/> — como o analyzer faz com a config: a IDE reaproveita a instância enquanto o
    /// arquivo não muda, então nem o suppressor (chamado a cada rodada de diagnósticos) nem o analyzer
    /// reparseiam. Compartilhado entre <see cref="ArchRulesAnalyzer"/> (reporta o <c>ARCH9009</c>) e
    /// <see cref="BaselineSuppressor"/> (suprime), por isso estático. Sem I/O: só lê o texto que o host já deu.
    /// </summary>
    internal static class BaselineCache
    {
        private static readonly ConditionalWeakTable<SourceText, BaselineAnalysis> Cache =
            new ConditionalWeakTable<SourceText, BaselineAnalysis>();

        internal static BaselineAnalysis Get(SourceText text)
        {
            return Cache.GetValue(text, t => new BaselineAnalysis(BaselineFile.Parse(t.ToString())));
        }
    }

    /// <summary>Resultado imutável da análise de um texto de baseline, com busca O(1) por entrada.</summary>
    internal sealed class BaselineAnalysis
    {
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);

        internal BaselineAnalysis(BaselineParseResult result)
        {
            Errors = result.Errors;
            IsValid = result.IsValid;

            foreach (var entry in result.Entries)
            {
                _keys.Add(Key(entry.Rule, entry.File, entry.Subject));
            }
        }

        internal bool IsValid { get; }

        internal IReadOnlyList<SchemaValidationError> Errors { get; }

        /// <summary>
        /// Regra e assunto casam por igualdade ordinal; o caminho (relativo ao diretório do baseline) ignora
        /// caixa e aceita <c>\</c> ou <c>/</c>. Baseline inválido nunca contém nada.
        /// </summary>
        internal bool Contains(string rule, string relativeFile, string subject)
        {
            return IsValid && _keys.Contains(Key(rule, relativeFile, subject));
        }

        private static string Key(string rule, string file, string subject)
        {
            return rule + "\0" + BaselineFile.NormalizeFile(file).ToUpperInvariant() + "\0" + subject;
        }
    }
}
