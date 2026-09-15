using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// O pool de slots é o contrato que o resto da lib assume (ADR-001 §3, Opção C). Um slot que
    /// deixe de existir em <c>SupportedDiagnostics</c> não falha alto: o diagnóstico simplesmente
    /// vira AD0001 e some. Por isso o formato do pool é testado explicitamente.
    /// </summary>
    public class ArchDiagnosticsTests
    {
        // ARCH9006/ARCH9007 entraram com a resolução de `extends` dentro do analyzer: um código de
        // motor que não esteja aqui (e, portanto, em SupportedDiagnostics) nunca chega à Error List —
        // vira AD0001 silencioso, o modo de falha que esta classe inteira existe para vigiar.
        private static readonly string[] EngineIds =
        {
            "ARCH9001", "ARCH9002", "ARCH9003", "ARCH9004", "ARCH9005", "ARCH9006", "ARCH9007"
        };

        [Fact]
        public void Declara_512_slots_mais_a_faixa_reservada_do_motor()
        {
            var all = new ArchRulesAnalyzer().SupportedDiagnostics;

            Assert.Equal(512, ArchDiagnostics.SlotCount);
            Assert.Equal(ArchDiagnostics.SlotCount + EngineIds.Length, all.Length);
            Assert.Equal(all.Length, all.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
        }

        [Theory]
        [InlineData("ARCH0001")]
        [InlineData("ARCH0007")]
        [InlineData("ARCH0512")]
        public void Slot_do_pool_existe_com_o_default_esperado(string slotId)
        {
            DiagnosticDescriptor descriptor;
            Assert.True(ArchDiagnostics.TryGetSlot(slotId, out descriptor));
            Assert.Equal(slotId, descriptor.Id);

            // Warning é só o DEFAULT do slot; a severidade real vem do .editorconfig/.globalconfig
            // gerado a partir do YAML (ADR-001 D3).
            Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
            Assert.True(descriptor.IsEnabledByDefault);
            Assert.Contains(descriptor, new ArchRulesAnalyzer().SupportedDiagnostics);
        }

        [Theory]
        [InlineData("ARCH0000")]
        [InlineData("ARCH0513")]
        [InlineData("ARCH9001")] // faixa do motor não é alocável por configuração
        [InlineData("ACME-001")] // ID de negócio nunca é um slot
        [InlineData("")]
        [InlineData(null)]
        public void Valor_fora_do_pool_nao_vira_slot(string slotId)
        {
            DiagnosticDescriptor descriptor;
            Assert.False(ArchDiagnostics.TryGetSlot(slotId, out descriptor));
            Assert.Null(descriptor);
        }

        [Fact]
        public void Faixa_do_motor_e_habilitada_por_padrao_e_permanece_silenciavel()
        {
            var byId = new ArchRulesAnalyzer().SupportedDiagnostics.ToDictionary(d => d.Id, StringComparer.Ordinal);

            foreach (var id in EngineIds)
            {
                Assert.True(byId.ContainsKey(id), "Faltou declarar " + id);
                Assert.True(byId[id].IsEnabledByDefault);

                // ADR-001 D4: sem NotConfigurable — a tag desligaria #pragma e .editorconfig, e o
                // time precisa poder silenciar um aviso do motor pontualmente.
                Assert.DoesNotContain(WellKnownDiagnosticTags.NotConfigurable, byId[id].CustomTags);
            }

            Assert.Equal(DiagnosticSeverity.Error, byId["ARCH9001"].DefaultSeverity);
            Assert.Equal(DiagnosticSeverity.Warning, byId["ARCH9002"].DefaultSeverity);
            Assert.Equal(DiagnosticSeverity.Warning, byId["ARCH9003"].DefaultSeverity);
            Assert.Equal(DiagnosticSeverity.Warning, byId["ARCH9004"].DefaultSeverity);
            Assert.Equal(DiagnosticSeverity.Warning, byId["ARCH9006"].DefaultSeverity);
            Assert.Equal(DiagnosticSeverity.Warning, byId["ARCH9007"].DefaultSeverity);
        }

        [Fact]
        public void Nenhum_slot_do_pool_e_marcado_como_nao_configuravel()
        {
            // Override por regra via `dotnet_diagnostic.ARCH0007.severity` é o motivo de existir do
            // pool; NotConfigurable em qualquer slot mataria exatamente isso.
            Assert.All(
                new ArchRulesAnalyzer().SupportedDiagnostics,
                d => Assert.DoesNotContain(WellKnownDiagnosticTags.NotConfigurable, d.CustomTags));
        }
    }
}
