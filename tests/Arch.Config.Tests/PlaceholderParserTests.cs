using System.Linq;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>Issue #24 — placeholders <c>[Nome]</c> nos critérios e condição <c>when</c> nas regras.</summary>
    public class PlaceholderParserTests
    {
        private const string Header = "schema: arch-rules/v1\n";

        private const string ModuleLayers = @"
layers:
  ModuleServices:
    match: [ { namespace: ""Acme.[Module].Services.**"" } ]
  ModuleDomain:
    match: [ { namespace: ""Acme.[Module].Domain.**"" } ]
";

        private static string Rule(string when, string type = "forbidden-call", string id = "ACME-010", string slot = "ARCH0010")
            => $@"
  - id: {id}
    slot: {slot}
    type: {type}
    from: ModuleServices
    to: ModuleDomain
{(when == null ? string.Empty : "    when: " + when + "\n")}";

        // ---- placeholders nos critérios --------------------------------------------------------

        [Fact]
        public void Placeholder_valido_em_namespace_e_assembly_e_aceito()
        {
            var yaml = Header + @"
layers:
  A:
    match:
      - namespace: ""Acme.[Module].Services.**""
      - assembly: ""Acme.[Tenant].Core""
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            var layer = result.Config.Layers["A"];
            Assert.Equal("Acme.[Module].Services.**", layer.Match[0].Namespace);
            Assert.Equal("Acme.[Tenant].Core", layer.Match[1].Assembly);
            Assert.Equal(new[] { "Module", "Tenant" }, layer.CaptureNames.ToArray());
        }

        [Theory]
        [InlineData("Acme.[Module].*.**")]
        [InlineData("**.[M1].Domain")]
        [InlineData("[A].[B].[C2]")]
        public void Placeholder_combina_com_curingas(string pattern)
        {
            var yaml = Header + $"layers:\n  A:\n    match: [ {{ namespace: \"{pattern}\" }} ]\n";

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
        }

        [Theory]
        [InlineData("Acme.[Module.Services")]     // colchete sem fechar
        [InlineData("Acme.Module].Services")]     // colchete sem abrir
        [InlineData("Acme.[].Services")]          // nome vazio
        [InlineData("Acme.[1Module].Services")]   // começa por dígito
        [InlineData("Acme.[Mod-ule].Services")]   // caractere inválido
        [InlineData("Acme.[Mod_ule].Services")]   // underscore não é letra/dígito
        [InlineData("Acme.My[Module].Services")]  // placeholder misturado com texto
        [InlineData("Acme.[Module]X.Services")]
        [InlineData("Acme.[[Module]].Services")]
        public void Placeholder_malformado_em_namespace_e_erro_bloqueante_com_posicao(string pattern)
        {
            var yaml = Header + $"layers:\n  A:\n    match: [ {{ namespace: \"{pattern}\" }} ]\n";

            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
            Assert.Equal(4, error.Line);
            Assert.True(error.Column > 0);
        }

        [Fact]
        public void Placeholder_malformado_em_assembly_e_em_exclude_tambem_e_erro_bloqueante()
        {
            var inAssembly = Header + "layers:\n  A:\n    match: [ { assembly: \"Acme.[Mod\" } ]\n";
            var inExclude = Header + "layers:\n  A:\n    match: [ { nameSuffix: X } ]\n    exclude: [ { namespace: \"Acme.[]\" } ]\n";

            foreach (var yaml in new[] { inAssembly, inExclude })
            {
                var result = ArchConfigParser.Parse(yaml);

                Assert.Null(result.Config);
                Assert.Equal("ARCH9001", Assert.Single(result.Errors).Code);
            }
        }

        [Fact]
        public void Placeholder_repetido_no_mesmo_padrao_e_erro_bloqueante()
        {
            var yaml = Header + "layers:\n  A:\n    match: [ { namespace: \"Acme.[Module].Sub.[Module]\" } ]\n";

            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.Contains("Module", error.Message);
        }

        [Fact]
        public void Mesmo_nome_em_criterios_diferentes_da_mesma_camada_e_aceito()
        {
            var yaml = Header + @"
layers:
  A:
    match:
      - namespace: ""Acme.[Module].Services.**""
      - assembly: ""Acme.[Module]""
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            Assert.Equal(new[] { "Module" }, result.Config.Layers["A"].CaptureNames.ToArray());
        }

        [Fact]
        public void Colchetes_so_sao_validados_em_namespace_e_assembly()
        {
            // nameSuffix/baseType/implements/attribute não têm sintaxe de placeholder: não mudam.
            var yaml = Header + "layers:\n  A:\n    match: [ { nameSuffix: \"[Odd\" } ]\n";

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
        }

        // ---- when ------------------------------------------------------------------------------

        [Fact]
        public void When_differ_com_lista_e_tipado_e_serializado_no_extra()
        {
            var yaml = Header + ModuleLayers + "rules:" + Rule("{ differ: [Module] }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            var rule = Assert.Single(result.Config.Rules);
            Assert.Equal("differ=Module", rule.Extra["when"]);
            Assert.True(rule.TryGetKeyList("when", out var key, out var names));
            Assert.Equal("differ", key);
            Assert.Equal(new[] { "Module" }, names.ToArray());
        }

        [Fact]
        public void When_same_com_varios_nomes_segue_a_convencao_de_lista_sem_espaco()
        {
            var yaml = Header + @"
layers:
  ModuleServices:
    match: [ { namespace: ""Acme.[Module].[Tenant].Services.**"" } ]
  ModuleDomain:
    match: [ { namespace: ""Acme.[Module].[Tenant].Domain.**"" } ]
rules:" + Rule("{ same: [Module, Tenant] }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            var rule = Assert.Single(result.Config.Rules);
            Assert.Equal("same=Module,Tenant", rule.Extra["when"]);
            Assert.True(rule.TryGetKeyList("when", out var key, out var names));
            Assert.Equal("same", key);
            Assert.Equal(new[] { "Module", "Tenant" }, names.ToArray());
        }

        [Fact]
        public void When_aceita_escalar_unico_como_lista_de_um_item()
        {
            var yaml = Header + ModuleLayers + "rules:" + Rule("{ same: Module }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            Assert.Equal("same=Module", Assert.Single(result.Config.Rules).Extra["when"]);
        }

        [Fact]
        public void When_vale_tambem_para_must_route_through()
        {
            var yaml = Header + ModuleLayers + "rules:" + Rule("{ differ: [Module] }", "must-route-through");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            Assert.Equal("differ=Module", Assert.Single(result.Config.Rules).Extra["when"]);
        }

        [Theory]
        [InlineData("{ same: [Module], differ: [Module] }")] // duas chaves
        [InlineData("{ equal: [Module] }")]                  // chave desconhecida
        [InlineData("{ same: [] }")]                         // lista vazia
        [InlineData("{ same: [Module, \"\"] }")]             // item vazio
        [InlineData("{}")]                                   // nenhuma chave
        [InlineData("Module")]                               // não é mapeamento
        public void When_com_forma_invalida_e_ARCH9008_e_a_regra_e_descartada(string when)
        {
            var yaml = Header + ModuleLayers + "rules:" + Rule(when);

            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            Assert.Empty(result.Config.Rules);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            Assert.False(error.IsBlocking);
            Assert.Contains("when", error.Message);
        }

        [Fact]
        public void When_nao_e_aceito_por_tipos_que_nao_o_declaram()
        {
            var yaml = Header + ModuleLayers + @"
rules:
  - id: ACME-011
    slot: ARCH0011
    type: naming-convention
    layer: ModuleServices
    require: { suffix: Service }
    when: { same: [Module] }
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Config.Rules);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            Assert.Contains("when", error.Message);
        }

        // ---- when x capturas das camadas -------------------------------------------------------

        [Fact]
        public void When_com_nome_nao_capturado_por_nenhuma_das_camadas_e_ARCH9008_com_posicao_da_regra()
        {
            var yaml = Header + ModuleLayers + "rules:" + Rule("{ differ: [Tenant] }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            Assert.Empty(result.Config.Rules);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            Assert.False(error.IsBlocking);
            Assert.Contains("Tenant", error.Message);
            Assert.Contains("ACME-010", error.Message);
            Assert.Equal(9, error.Line); // linha do item da regra
            Assert.True(error.Column > 0);
            Assert.Equal(0, error.SourceDocumentIndex);
        }

        [Fact]
        public void When_com_um_nome_valido_e_outro_nao_capturado_descarta_a_regra()
        {
            var yaml = Header + ModuleLayers + "rules:" + Rule("{ differ: [Module, Tenant] }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Config.Rules);
            Assert.Equal("ARCH9008", Assert.Single(result.Errors).Code);
        }

        [Fact]
        public void When_com_nome_capturado_so_pela_camada_to_e_aceito()
        {
            // Regra do contrato: ARCH9008 só quando NENHUMA das duas camadas captura o nome.
            var yaml = Header + @"
layers:
  ModuleServices:
    match: [ { namespace: ""Acme.Services.**"" } ]
  ModuleDomain:
    match: [ { namespace: ""Acme.[Module].Domain.**"" } ]
rules:" + Rule("{ differ: [Module] }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            Assert.Single(result.Config.Rules);
        }

        [Fact]
        public void When_com_camada_inexistente_e_ARCH9008()
        {
            var yaml = Header + "layers:\n  Other:\n    match: [ { nameSuffix: X } ]\nrules:" + Rule("{ differ: [Module] }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Config.Rules);
            Assert.Equal("ARCH9008", Assert.Single(result.Errors).Code);
        }

        [Fact]
        public void Captura_no_exclude_nao_conta_para_o_when()
        {
            var yaml = Header + @"
layers:
  ModuleServices:
    match: [ { nameSuffix: Service } ]
    exclude: [ { namespace: ""Acme.[Module].Legacy.**"" } ]
  ModuleDomain:
    match: [ { nameSuffix: Entity } ]
rules:" + Rule("{ differ: [Module] }");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Config.Rules);
            Assert.Equal("ARCH9008", Assert.Single(result.Errors).Code);
        }

        [Fact]
        public void When_invalido_nao_derruba_as_outras_regras()
        {
            var yaml = Header + ModuleLayers + "rules:"
                + Rule("{ differ: [Nope] }", id: "ACME-010", slot: "ARCH0010")
                + Rule("{ differ: [Module] }", id: "ACME-012", slot: "ARCH0012");

            var result = ArchConfigParser.Parse(yaml);

            Assert.Equal("ACME-012", Assert.Single(result.Config.Rules).Id);
            Assert.Equal("ARCH9008", Assert.Single(result.Errors).Code);
        }

        [Fact]
        public void Validacao_do_when_usa_as_camadas_ja_mescladas_do_extends()
        {
            var baseYaml = Header + ModuleLayers;
            var childYaml = Header + "rules:" + Rule("{ differ: [Module] }");

            var result = ArchConfigParser.Parse(new[] { baseYaml, childYaml });

            Assert.Empty(result.Errors);
            Assert.Single(result.Config.Rules);
        }

        [Fact]
        public void Camada_do_filho_substitui_a_do_base_e_o_when_e_revalidado_contra_ela()
        {
            var baseYaml = Header + ModuleLayers;
            var childYaml = Header
                + "layers:\n  ModuleDomain:\n    match: [ { namespace: \"Acme.Domain.**\" } ]\n"
                + "  ModuleServices:\n    match: [ { namespace: \"Acme.Services.**\" } ]\n"
                + "rules:" + Rule("{ differ: [Module] }");

            var result = ArchConfigParser.Parse(new[] { baseYaml, childYaml });

            Assert.Empty(result.Config.Rules);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            Assert.Equal(1, error.SourceDocumentIndex); // regra declarada no arquivo do filho
        }

        [Fact]
        public void Regra_do_base_com_when_invalido_aponta_para_o_documento_do_base()
        {
            var baseYaml = Header + "rules:" + Rule("{ differ: [Module] }");
            var childYaml = Header + ModuleLayers.Replace("[Module]", "X");

            var result = ArchConfigParser.Parse(new[] { baseYaml, childYaml });

            Assert.Empty(result.Config.Rules);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            Assert.Equal(0, error.SourceDocumentIndex);
        }
    }
}
