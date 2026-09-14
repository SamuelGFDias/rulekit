using System;
using System.Collections.Generic;
using System.Linq;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;

namespace Arch.Rules
{
    /// <summary>
    /// Avaliador do tipo `max-dependencies` (ADR-001 §4 D2): limita quantas dependências um tipo de
    /// uma camada pode ter, contadas conforme `countBy` (`constructor-parameters`, `fields` ou
    /// `all`). Usa <see cref="ILayerResolver.IsInLayer"/> para restringir a checagem à camada
    /// declarada em `rule.Extra["layer"]` e reporta no slot pré-declarado quando a contagem excede
    /// `rule.Extra["max"]`.
    ///
    /// <para><b>Decisões de design não detalhadas pelo ADR (documentadas aqui, não no ADR):</b></para>
    /// <list type="bullet">
    /// <item>
    /// <b>Múltiplos construtores públicos</b> (`constructor-parameters`/`all`): usa o construtor
    /// público com MAIS parâmetros. Racional: o "pior caso" de acoplamento do tipo é o maior
    /// construtor público disponível a quem o instancia — usar o menor esconderia o problema, e
    /// somar todos os construtores puniria sobrecargas de conveniência (ex.: um construtor que só
    /// delega para o maior com valores default).
    /// </item>
    /// <item>
    /// <b>Contagem por TIPO distinto, não por ocorrência.</b> Tanto para parâmetros de construtor
    /// quanto para campos, dois parâmetros/campos do mesmo tipo contam como UMA dependência — é o
    /// tipo que representa o acoplamento real (dois `ILogger` não são duas dependências
    /// arquiteturais diferentes).
    /// </item>
    /// <item>
    /// <b>`fields`</b>: conta apenas campos de instância `private`/`protected` DECLARADOS
    /// DIRETAMENTE no tipo (não herdados — <see cref="INamedTypeSymbol.GetMembers()"/> já não traz
    /// membros herdados), cujo tipo não seja primitivo/`string`/enum — esses são dados de valor, não
    /// dependências de colaboração. Campos estáticos são excluídos: representam estado
    /// compartilhado/config, não uma dependência por instância.
    /// </item>
    /// <item>
    /// <b>`all`</b>: união dos tipos distintos das duas contagens acima (não soma). Isso evita contar
    /// duas vezes o padrão comum de DI `private readonly IFoo _foo` atribuído a partir de um
    /// parâmetro `IFoo foo` do construtor — o mesmo tipo `IFoo` conta uma única vez.
    /// </item>
    /// <item>
    /// <b>`countBy` desconhecido</b>: a regra não reporta nada. Validar que `countBy` é um dos
    /// três valores aceitos é responsabilidade do schema (Arch.Config/1A), não deste avaliador —
    /// mesmo raciocínio do `ARCH9003` para `type` desconhecido (AGENTS.md §8).
    /// </item>
    /// <item>
    /// <b>Limite inclusivo.</b> `max: N` significa "no máximo N", não "menos que N": contagem
    /// EXATAMENTE igual a `max` não reporta. É a leitura natural de "max" como teto permitido, e a
    /// única que não soa como off-by-one para quem escreve a política.
    /// </item>
    /// </list>
    /// </summary>
    public sealed class MaxDependenciesEvaluator : IRuleEvaluator
    {
        private const string CountByConstructorParameters = "constructor-parameters";
        private const string CountByFields = "fields";
        private const string CountByAll = "all";

        public string RuleType => "max-dependencies";

        public void RegisterActions(
            Microsoft.CodeAnalysis.Diagnostics.CompilationStartAnalysisContext context,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor)
        {
            string layer;
            if (!rule.Extra.TryGetValue("layer", out layer) || string.IsNullOrEmpty(layer))
            {
                return;
            }

            string countBy;
            if (!rule.Extra.TryGetValue("countBy", out countBy) || string.IsNullOrEmpty(countBy))
            {
                return;
            }

            if (!string.Equals(countBy, CountByConstructorParameters, StringComparison.Ordinal)
                && !string.Equals(countBy, CountByFields, StringComparison.Ordinal)
                && !string.Equals(countBy, CountByAll, StringComparison.Ordinal))
            {
                // countBy malformado: regra malformada. Validar isso é responsabilidade do schema
                // (Arch.Config/1A) — este avaliador simplesmente não reporta nada.
                return;
            }

            string maxRaw;
            int max;
            if (!rule.Extra.TryGetValue("max", out maxRaw) || !int.TryParse(maxRaw, out max))
            {
                return;
            }

            context.RegisterSymbolAction(
                symbolContext => Analyze(symbolContext, rule, layerResolver, slotDescriptor, layer, countBy, max),
                SymbolKind.NamedType);
        }

        private static void Analyze(
            Microsoft.CodeAnalysis.Diagnostics.SymbolAnalysisContext symbolContext,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor,
            string layer,
            string countBy,
            int max)
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;

            if (!layerResolver.IsInLayer(type, layer))
            {
                return;
            }

            var dependencyTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

            if (string.Equals(countBy, CountByConstructorParameters, StringComparison.Ordinal)
                || string.Equals(countBy, CountByAll, StringComparison.Ordinal))
            {
                CollectConstructorParameterTypes(type, dependencyTypes);
            }

            if (string.Equals(countBy, CountByFields, StringComparison.Ordinal)
                || string.Equals(countBy, CountByAll, StringComparison.Ordinal))
            {
                CollectDependencyFieldTypes(type, dependencyTypes);
            }

            var count = dependencyTypes.Count;
            if (count <= max)
            {
                return;
            }

            symbolContext.ReportDiagnostic(Diagnostic.Create(
                slotDescriptor,
                type.Locations.FirstOrDefault() ?? Location.None,
                BuildMessage(rule, type.Name, count, max)));
        }

        private static void CollectConstructorParameterTypes(INamedTypeSymbol type, HashSet<ITypeSymbol> dependencyTypes)
        {
            var biggestPublicConstructor = type.Constructors
                .Where(ctor => !ctor.IsStatic
                    && ctor.MethodKind == MethodKind.Constructor
                    && ctor.DeclaredAccessibility == Accessibility.Public)
                .OrderByDescending(ctor => ctor.Parameters.Length)
                .FirstOrDefault();

            if (biggestPublicConstructor == null)
            {
                return;
            }

            foreach (var parameter in biggestPublicConstructor.Parameters)
            {
                dependencyTypes.Add(parameter.Type);
            }
        }

        private static void CollectDependencyFieldTypes(INamedTypeSymbol type, HashSet<ITypeSymbol> dependencyTypes)
        {
            foreach (var member in type.GetMembers())
            {
                var field = member as IFieldSymbol;
                if (field == null || field.IsStatic || field.IsImplicitlyDeclared)
                {
                    continue;
                }

                if (field.DeclaredAccessibility != Accessibility.Private
                    && field.DeclaredAccessibility != Accessibility.Protected)
                {
                    continue;
                }

                if (IsValueLikeType(field.Type))
                {
                    continue;
                }

                dependencyTypes.Add(field.Type);
            }
        }

        private static bool IsValueLikeType(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Char:
                case SpecialType.System_Decimal:
                case SpecialType.System_Double:
                case SpecialType.System_Single:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_String:
                case SpecialType.System_DateTime:
                    return true;
                default:
                    return false;
            }
        }

        internal static string BuildMessage(RuleDefinition rule, string typeName, int count, int max)
            => rule.Id + ": '" + typeName + "' tem " + count + " dependência(s), máximo permitido é " + max;
    }
}
