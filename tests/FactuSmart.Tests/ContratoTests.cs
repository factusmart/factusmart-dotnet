using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace FactuSmart.Tests
{
    /// <summary>
    /// Prueba de contrato (8): cada operationId de openapi.yaml debe tener su método en el SDK.
    /// Si el API crece y nadie actualiza el SDK, esta prueba falla.
    /// Ruta del yaml: variable FACTUSMART_OPENAPI o tests/fixtures/openapi.yaml.
    /// </summary>
    public class ContratoTests
    {
        /// <summary>operationId → ruta del método ("Recurso.Metodo" en la instancia, o "FactuSmartClient.Metodo" estático).</summary>
        public static readonly IReadOnlyDictionary<string, string> Metodos = new Dictionary<string, string>
        {
            ["ping"] = "PingAsync",
            ["getAccount"] = "Cuenta.SaldoAsync",
            ["listSeries"] = "Series.ListarAsync",
            ["emitDocument"] = "Comprobantes.EmitirAsync",
            ["listDocuments"] = "Comprobantes.ListarAsync",
            ["getDocument"] = "Comprobantes.ConsultarAsync",
            ["downloadDocumentFile"] = "Comprobantes.DescargarAsync",
            ["resendDocument"] = "Comprobantes.ReenviarAsync",
            ["querySunatStatus"] = "Comprobantes.ConsultarSunatAsync",
            ["getDocumentHistory"] = "Comprobantes.HistorialAsync",
            ["voidDocument"] = "Anulaciones.CrearAsync",
            ["getVoidStatus"] = "Anulaciones.EstadoAsync",
            ["emitDispatchGuide"] = "Guias.EmitirAsync",
            ["getDispatchGuideStatus"] = "Guias.EstadoAsync",
            ["listRucs"] = "Rucs.ListarAsync",
            ["createRuc"] = "Rucs.CrearAsync",
            ["setRucActivation"] = "Rucs.ActivarAsync",
            ["calculateDocument"] = "Comprobantes.CalcularAsync",
            ["listCatalogs"] = "Catalogos.ListarAsync",
            ["getCatalog"] = "Catalogos.ConsultarAsync",
            // Webhook (sección `webhooks` del yaml): lo manda el API; el SDK solo verifica la firma.
            ["webhookComprobanteEstado"] = "FactuSmartClient.VerificarFirmaWebhook",
        };

        [Fact]
        public void Todos_los_operationId_del_openapi_tienen_metodo_en_el_SDK()
        {
            var ruta = Environment.GetEnvironmentVariable("FACTUSMART_OPENAPI");
            if (string.IsNullOrEmpty(ruta)) ruta = Path.Combine(AppContext.BaseDirectory, "fixtures", "openapi.yaml");
            Assert.True(File.Exists(ruta), $"no existe {ruta}");

            var yaml = File.ReadAllText(ruta);
            var ids = Regex.Matches(yaml, @"^\s*operationId:\s*['""]?([A-Za-z0-9_]+)['""]?\s*$", RegexOptions.Multiline)
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(ids);

            var faltan = new List<string>();
            foreach (var id in ids)
            {
                if (!Metodos.TryGetValue(id, out var metodo)) { faltan.Add($"{id} (sin método asignado en el SDK)"); continue; }
                if (!Existe(metodo)) faltan.Add($"{id} → {metodo} (no existe)");
            }
            Assert.True(faltan.Count == 0, "operationId sin método en el SDK:\n  " + string.Join("\n  ", faltan));

            var sobran = Metodos.Keys.Where(k => !ids.Contains(k)).ToList();
            Assert.True(sobran.Count == 0, "métodos del SDK cuyo operationId ya no está en el yaml: " + string.Join(", ", sobran));
            Assert.Equal(21, ids.Count);
        }

        private static bool Existe(string ruta)
        {
            var partes = ruta.Split('.');
            if (partes[0] == nameof(FactuSmartClient))
                return typeof(FactuSmartClient).GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == partes[1]);
            var tipo = typeof(FactuSmartClient);
            for (var i = 0; i < partes.Length - 1; i++)
            {
                var prop = tipo.GetProperty(partes[i], BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return false;
                tipo = prop.PropertyType;
            }
            return tipo.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == partes[partes.Length - 1]);
        }
    }
}
