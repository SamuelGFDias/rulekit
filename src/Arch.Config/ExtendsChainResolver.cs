using System;
using System.Collections.Generic;
using System.Threading;

namespace Arch.Config
{
    /// <summary>Tipo de problema encontrado ao resolver uma cadeia de <c>extends</c>.</summary>
    public enum ExtendsChainProblemKind
    {
        /// <summary>O loader devolveu <c>null</c> para o caminho resolvido: o arquivo não existe
        /// (no disco, para o CLI; em @(AdditionalFiles), para o analyzer). O ramo é descartado.</summary>
        Missing,

        /// <summary>O caminho resolvido já está na pilha da raiz até o arquivo corrente. O ramo que
        /// fecharia o ciclo é cortado.</summary>
        Cycle,

        /// <summary>Descer neste <c>extends</c> excederia o limite de profundidade ou de textos. O
        /// ramo é cortado.</summary>
        DepthExceeded,
    }

    /// <summary>Um problema de resolução de <c>extends</c>, com os caminhos envolvidos.</summary>
    public sealed class ExtendsChainProblem
    {
        public ExtendsChainProblemKind Kind { get; }

        /// <summary>Caminho (na forma devolvida pelo combinador) do arquivo que declarou o <c>extends</c>.</summary>
        public string DeclaringPath { get; }

        /// <summary>O caminho EXATAMENTE como escrito em <c>extends:</c>.</summary>
        public string DeclaredPath { get; }

        /// <summary>Resultado do combinador para <see cref="DeclaredPath"/> a partir de <see cref="DeclaringPath"/>.</summary>
        public string ResolvedPath { get; }

        /// <summary>Caminhos da raiz até <see cref="DeclaringPath"/> (inclusive), no momento do problema —
        /// é o que descreve um ciclo.</summary>
        public IReadOnlyList<string> Stack { get; }

        public ExtendsChainProblem(
            ExtendsChainProblemKind kind,
            string declaringPath,
            string declaredPath,
            string resolvedPath,
            IReadOnlyList<string> stack)
        {
            Kind = kind;
            DeclaringPath = declaringPath;
            DeclaredPath = declaredPath;
            ResolvedPath = resolvedPath;
            Stack = stack ?? Array.Empty<string>();
        }
    }

    /// <summary>Um arquivo da cadeia resolvida: caminho (forma do combinador) e texto.</summary>
    public sealed class ExtendsChainEntry
    {
        public string Path { get; }

        public string Text { get; }

        public ExtendsChainEntry(string path, string text)
        {
            Path = path;
            Text = text;
        }
    }

    /// <summary>Resultado de <see cref="ExtendsChainResolver.Resolve"/>.</summary>
    public sealed class ExtendsChainResult
    {
        /// <summary>Arquivos da cadeia em ordem CRESCENTE de precedência (base primeiro, raiz por
        /// último) — a mesma ordem de <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/>.</summary>
        public IReadOnlyList<ExtendsChainEntry> Entries { get; }

        /// <summary>Os textos de <see cref="Entries"/>, na mesma ordem — prontos para
        /// <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/>.</summary>
        public IReadOnlyList<string> Texts { get; }

        /// <summary>Problemas encontrados, na ordem em que apareceram na travessia.</summary>
        public IReadOnlyList<ExtendsChainProblem> Problems { get; }

        public ExtendsChainResult(IReadOnlyList<ExtendsChainEntry> entries, IReadOnlyList<ExtendsChainProblem> problems)
        {
            Entries = entries ?? Array.Empty<ExtendsChainEntry>();
            Problems = problems ?? Array.Empty<ExtendsChainProblem>();

            var texts = new string[Entries.Count];
            for (var i = 0; i < texts.Length; i++)
            {
                texts[i] = Entries[i].Text;
            }

            Texts = texts;
        }
    }

    /// <summary>
    /// Resolvedor ÚNICO da cadeia de <c>extends</c> (ADR-001 D2, issue #18), usado pelo analyzer
    /// (contra @(AdditionalFiles), sem I/O — RS1035) e pelo CLI (contra o disco). Não faz I/O nem
    /// manipula caminho por conta própria: quem chama fornece o <c>loader</c> (caminho → texto, ou
    /// <c>null</c> quando ausente) e o <c>combinePath</c> (arquivo que declara + relativo → caminho
    /// resolvido, na mesma forma normalizada do <c>rootPath</c>). A extração de <c>extends:</c> é
    /// sempre <see cref="ArchConfigParser.PeekExtends"/> — uma única fonte da forma aceita.
    ///
    /// <para><b>Semântica:</b> travessia em profundidade e PÓS-ORDEM — cada arquivo estendido entra
    /// na lista antes de quem o estende, o que produz a ordem crescente de precedência ("o arquivo
    /// que faz extends vence"). Relativos reancoram no arquivo que os declara, não na raiz. Para
    /// cada <c>extends</c>, na ordem: (1) ciclo — o alvo já está na pilha da raiz até o arquivo
    /// corrente; (2) limite — <c>profundidade + 1 &gt;= maxDepth</c> ou textos já coletados
    /// <c>&gt;= maxTexts</c> (grafos em diamante crescem exponencialmente mesmo sem ciclo); (3)
    /// ausente — o loader devolveu <c>null</c>. Os três cortam só o RAMO; a resolução continua e o
    /// problema é devolvido tipado. A política de erro (warning não bloqueante no analyzer,
    /// bloqueante no CLI) é do chamador.</para>
    ///
    /// <para>Exceções do <c>loader</c>/<c>combinePath</c> e <see cref="OperationCanceledException"/>
    /// propagam: tratá-las também é política do chamador.</para>
    /// </summary>
    public static class ExtendsChainResolver
    {
        /// <summary>Profundidade máxima padrão (níveis de <c>extends</c> a partir da raiz).</summary>
        public const int DefaultMaxDepth = 32;

        /// <summary>Quantidade máxima padrão de textos coletados antes de parar de descer.</summary>
        public const int DefaultMaxTexts = 64;

        /// <summary>
        /// Normalização ÚNICA do caminho declarado em <c>extends:</c> (revisão B1): corta espaços/tabs
        /// nas pontas e troca <c>\</c> por <c>/</c>. O resolvedor a aplica ANTES de chamar o combinador,
        /// então analyzer e CLI recebem o mesmo relativo — em Linux, um <c>sub\base.yaml</c> ou um
        /// <c> base.yaml </c> não pode resolver num lado e não no outro. Puramente textual (sem I/O).
        /// </summary>
        public static string NormalizeDeclaredPath(string declaredPath)
        {
            return declaredPath == null
                ? string.Empty
                : declaredPath.Trim().Replace('\\', '/');
        }

        /// <param name="rootPath">Caminho do arquivo raiz, já na forma que <paramref name="combinePath"/> produz.</param>
        /// <param name="rootText">Texto do arquivo raiz (o chamador já o leu).</param>
        /// <param name="loader">Caminho resolvido → texto, ou <c>null</c> quando o arquivo não existe.</param>
        /// <param name="combinePath">(caminho do arquivo que declara, relativo como escrito) → caminho resolvido.</param>
        /// <param name="pathComparer">Igualdade de caminhos para detecção de ciclo; padrão
        /// <see cref="StringComparer.OrdinalIgnoreCase"/>.</param>
        /// <param name="maxDepth">Ver <see cref="DefaultMaxDepth"/>.</param>
        /// <param name="maxTexts">Ver <see cref="DefaultMaxTexts"/>.</param>
        /// <param name="cancellationToken">Verificado a cada arquivo visitado.</param>
        public static ExtendsChainResult Resolve(
            string rootPath,
            string rootText,
            Func<string, string> loader,
            Func<string, string, string> combinePath,
            IEqualityComparer<string> pathComparer = null,
            int maxDepth = DefaultMaxDepth,
            int maxTexts = DefaultMaxTexts,
            CancellationToken cancellationToken = default)
        {
            if (rootPath == null)
            {
                throw new ArgumentNullException(nameof(rootPath));
            }

            if (rootText == null)
            {
                throw new ArgumentNullException(nameof(rootText));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (combinePath == null)
            {
                throw new ArgumentNullException(nameof(combinePath));
            }

            var state = new State(
                loader,
                combinePath,
                pathComparer ?? StringComparer.OrdinalIgnoreCase,
                maxDepth,
                maxTexts,
                cancellationToken);

            Visit(state, rootPath, rootText, 0);

            return new ExtendsChainResult(state.Entries, state.Problems);
        }

        private static void Visit(State state, string path, string text, int depth)
        {
            state.CancellationToken.ThrowIfCancellationRequested();

            state.Stack.Add(path);

            foreach (var declared in ArchConfigParser.PeekExtends(text))
            {
                if (string.IsNullOrWhiteSpace(declared))
                {
                    continue;
                }

                var target = state.CombinePath(path, NormalizeDeclaredPath(declared));

                if (Contains(state.Stack, target, state.Comparer))
                {
                    state.Problems.Add(Problem(state, ExtendsChainProblemKind.Cycle, path, declared, target));
                    continue;
                }

                if (depth + 1 >= state.MaxDepth || state.Entries.Count >= state.MaxTexts)
                {
                    state.Problems.Add(Problem(state, ExtendsChainProblemKind.DepthExceeded, path, declared, target));
                    continue;
                }

                var targetText = state.Loader(target);
                if (targetText == null)
                {
                    state.Problems.Add(Problem(state, ExtendsChainProblemKind.Missing, path, declared, target));
                    continue;
                }

                Visit(state, target, targetText, depth + 1);
            }

            state.Entries.Add(new ExtendsChainEntry(path, text));
            state.Stack.RemoveAt(state.Stack.Count - 1);
        }

        private static ExtendsChainProblem Problem(
            State state, ExtendsChainProblemKind kind, string declaringPath, string declared, string target)
            => new ExtendsChainProblem(kind, declaringPath, declared, target, state.Stack.ToArray());

        private static bool Contains(List<string> paths, string path, IEqualityComparer<string> comparer)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (comparer.Equals(paths[i], path))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class State
        {
            internal readonly Func<string, string> Loader;
            internal readonly Func<string, string, string> CombinePath;
            internal readonly IEqualityComparer<string> Comparer;
            internal readonly int MaxDepth;
            internal readonly int MaxTexts;
            internal readonly CancellationToken CancellationToken;
            internal readonly List<ExtendsChainEntry> Entries = new List<ExtendsChainEntry>();
            internal readonly List<ExtendsChainProblem> Problems = new List<ExtendsChainProblem>();
            internal readonly List<string> Stack = new List<string>();

            internal State(
                Func<string, string> loader,
                Func<string, string, string> combinePath,
                IEqualityComparer<string> comparer,
                int maxDepth,
                int maxTexts,
                CancellationToken cancellationToken)
            {
                Loader = loader;
                CombinePath = combinePath;
                Comparer = comparer;
                MaxDepth = maxDepth;
                MaxTexts = maxTexts;
                CancellationToken = cancellationToken;
            }
        }
    }
}
