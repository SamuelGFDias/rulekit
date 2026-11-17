using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>
    /// Tipos compartilhados de Arch.Config introduzidos pelas issues #6, #17 e #19:
    /// <see cref="SlotPool"/>, <see cref="SeverityVocabulary"/>, <see cref="RuleTypeCatalog"/> e
    /// <see cref="RuleDefinition.Properties"/>.
    /// </summary>
    public class SharedVocabularyTests
    {
        // ---------------------------------------------------------------- SlotPool

        [Theory]
        [InlineData("ARCH0001", SlotParseStatus.Valid, 1)]
        [InlineData("arch0007", SlotParseStatus.Valid, 7)]
        [InlineData(" Arch0512 ", SlotParseStatus.Valid, 512)]
        [InlineData("ARCH0000", SlotParseStatus.OutOfRange, 0)]
        [InlineData("ARCH0513", SlotParseStatus.OutOfRange, 513)]
        [InlineData("ARCH9001", SlotParseStatus.OutOfRange, 9001)]
        [InlineData("ARCH7", SlotParseStatus.InvalidFormat, 0)]
        [InlineData("ARCH00007", SlotParseStatus.InvalidFormat, 0)]
        [InlineData("ACME-001", SlotParseStatus.InvalidFormat, 0)]
        [InlineData("ARCH١٢٣٤", SlotParseStatus.InvalidFormat, 0)]
        [InlineData("", SlotParseStatus.InvalidFormat, 0)]
        [InlineData(null, SlotParseStatus.InvalidFormat, 0)]
        public void SlotPool_Parse_ClassifiesAndNormalizes(string raw, SlotParseStatus expected, int expectedNumber)
        {
            Assert.Equal(expected, SlotPool.Parse(raw, out var number));
            Assert.Equal(expectedNumber, number);
            Assert.Equal(expected == SlotParseStatus.Valid, SlotPool.TryNormalize(raw, out var canonical));
            if (expected == SlotParseStatus.Valid)
            {
                Assert.Equal(SlotPool.Format(expectedNumber), canonical);
            }
        }

        [Fact]
        public void SlotPool_HasTheDocumentedSize()
        {
            Assert.Equal(512, SlotPool.Count);
            Assert.Equal("ARCH0512", SlotPool.Format(SlotPool.Count));
        }

        [Fact]
        public void Parser_StoresTheCanonicalSlot()
        {
            var result = ArchConfigParser.Parse(@"
schema: arch-rules/v1
rules:
  - id: ACME-007
    slot: ' arch0007 '
    type: forbidden-call
    from: Controller
    to: Repository
  - id: ACME-999
    slot: ARCH9999
    type: forbidden-call
    from: Controller
    to: Repository
");
            Assert.Empty(result.Errors);
            Assert.Equal("ARCH0007", result.Config.Rules[0].Slot);
            Assert.Equal("ARCH9999", result.Config.Rules[1].Slot); // fora do pool: fica como veio (ARCH9002 é do analyzer/CLI)
        }

        // ---------------------------------------------------------------- SeverityVocabulary

        [Fact]
        public void SeverityVocabulary_AcceptsOnlyAnalyzerConfigValues()
        {
            Assert.Equal(new[] { "error", "warning", "suggestion", "silent", "none" }, SeverityVocabulary.AllowedValues);
            Assert.False(SeverityVocabulary.IsValid("info"));
            Assert.False(SeverityVocabulary.IsValid("hidden"));
            Assert.False(SeverityVocabulary.IsValid("Error"));
            Assert.False(SeverityVocabulary.IsValid(null));
        }

        [Fact]
        public void SeverityVocabulary_Resolve_CoversEveryOutcome()
        {
            var config = new ArchConfig(
                "arch-rules/v1",
                new Dictionary<string, LayerDefinition>(),
                new RuleDefinition[0],
                new Dictionary<string, string> { ["restrita"] = "error", ["errada"] = "info" },
                defaultSeverityName: null);

            RuleDefinition Rule(string severity) => new RuleDefinition("R", "ARCH0001", "forbidden-call", true, severity, null, null, null as IReadOnlyDictionary<string, string>);

            var resolved = SeverityVocabulary.Resolve(config, Rule("restrita"));
            Assert.Equal(SeverityResolutionStatus.Resolved, resolved.Status);
            Assert.Equal("error", resolved.Value);

            Assert.Equal(SeverityResolutionStatus.InvalidValue, SeverityVocabulary.Resolve(config, Rule("errada")).Status);
            Assert.Equal(SeverityResolutionStatus.UnknownName, SeverityVocabulary.Resolve(config, Rule("inexistente")).Status);
            Assert.Equal(SeverityResolutionStatus.NotDeclared, SeverityVocabulary.Resolve(config, Rule(null)).Status);
        }

        // ---------------------------------------------------------------- RuleTypeCatalog

        [Fact]
        public void Catalog_HasTheFourV1Types_WithCanonicalLowercaseNames()
        {
            Assert.Equal(
                new[] { "forbidden-call", "max-dependencies", "must-route-through", "naming-convention" },
                RuleTypeCatalog.Default.TypeNames.OrderBy(n => n, System.StringComparer.Ordinal));

            Assert.True(RuleTypeCatalog.Default.TryGetType("  Max-Dependencies ", out var schema));
            Assert.Equal("max-dependencies", schema.Name);
            Assert.False(RuleTypeCatalog.Default.TryGetType("cycle-check", out _));
            Assert.False(RuleTypeCatalog.Default.TryGetType(null, out _));
        }

        [Fact]
        public void Catalog_DeclaresRequiredFieldsAndAllowedValues()
        {
            Assert.True(RuleTypeCatalog.Default.TryGetType(RuleTypeNames.MaxDependencies, out var maxDeps));
            Assert.True(maxDeps.TryGetField(RuleFieldNames.Max, out var max));
            Assert.Equal(RuleFieldKind.Integer, max.Kind);
            Assert.True(max.Required);
            Assert.True(maxDeps.TryGetField(RuleFieldNames.CountBy, out var countBy));
            Assert.Equal(new[] { "constructor-parameters", "fields", "all" }, countBy.AllowedValues);

            Assert.True(RuleTypeCatalog.Default.TryGetType(RuleTypeNames.MustRouteThrough, out var route));
            Assert.True(route.TryGetField(RuleFieldNames.Through, out var through));
            Assert.Equal(RuleFieldKind.List, through.Kind);
            Assert.False(through.Required);
        }

        // ---------------------------------------------------------------- Properties

        [Fact]
        public void Parser_ProducesTypedProperties_AndDerivedExtra()
        {
            var result = ArchConfigParser.Parse(@"
schema: arch-rules/v1
rules:
  - id: R2
    slot: ARCH0002
    type: must-route-through
    from: Controller
    to: Repository
    through: [Service, Facade]
  - id: R3
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }
  - id: R4
    slot: ARCH0004
    type: max-dependencies
    layer: Service
    max: 7
    countBy: fields
");
            Assert.Empty(result.Errors);
            var route = result.Config.Rules[0];
            var naming = result.Config.Rules[1];
            var maxDeps = result.Config.Rules[2];

            Assert.Equal(RuleFieldKind.List, route.Properties["through"].Kind);
            Assert.Equal(new[] { "Service", "Facade" }, route.GetList("through"));
            Assert.Equal("Service,Facade", route.Extra["through"]);
            Assert.Equal("Controller", route.GetString("from"));

            Assert.True(naming.TryGetKeyValue("require", out var key, out var value));
            Assert.Equal(("suffix", "Repository"), (key, value));
            Assert.Equal("suffix=Repository", naming.Extra["require"]);

            Assert.Equal(RuleFieldKind.Integer, maxDeps.Properties["max"].Kind);
            Assert.True(maxDeps.TryGetInt("max", out var max));
            Assert.Equal(7, max);
            Assert.Equal("7", maxDeps.Extra["max"]);
        }

        [Fact]
        public void RuleBuiltFromExtraOnly_ExposesScalarProperties_ReadableByTheAccessors()
        {
            var rule = new RuleDefinition(
                "R",
                "ARCH0001",
                "max-dependencies",
                true,
                null,
                null,
                null,
                new Dictionary<string, string>
                {
                    ["through"] = "Service, Facade,",
                    ["max"] = "3",
                    ["require"] = " prefix = I ",
                });

            Assert.All(rule.Properties.Values, v => Assert.Equal(RuleFieldKind.Scalar, v.Kind));
            Assert.Equal(new[] { "Service", "Facade" }, rule.GetList("through"));
            Assert.True(rule.TryGetInt("max", out var max));
            Assert.Equal(3, max);
            Assert.True(rule.TryGetKeyValue("require", out var key, out var value));
            Assert.Equal(("prefix", "I"), (key, value));
            Assert.Null(rule.GetString("missing"));
            Assert.Empty(rule.GetList("missing"));
            Assert.False(rule.TryGetInt("through", out _));
        }

        [Fact]
        public void LegacyScalarRequire_IsStillAccepted()
        {
            var result = ArchConfigParser.Parse(@"
schema: arch-rules/v1
rules:
  - id: R3
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: suffix=Repository
");
            Assert.Empty(result.Errors);
            Assert.Equal("suffix=Repository", result.Config.Rules[0].Extra["require"]);
        }
    }
}
