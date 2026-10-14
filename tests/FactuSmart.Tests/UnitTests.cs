using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static FactuSmart.Tests.Ayuda;

namespace FactuSmart.Tests
{
    /// <summary>Pruebas sin red: HttpMessageHandler falso.</summary>
    public class UnitTests
    {
        private static readonly Regex UuidV4 = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase);

        private static readonly object Boleta = new
        {
            codigo_tipo_documento = "03",
            datos_del_cliente_o_receptor = new { codigo_tipo_documento_identidad = "1", numero_documento = "12345678" },
            items = new object[0],
        };

        // ───── Constructor ─────

        [Fact]
        public void Constructor_sin_ApiKey_o_BaseUrl_lanza_error_claro()
        {
            var e1 = Assert.Throws<FactuSmartException>(() => new FactuSmartClient(new FactuSmartOptions { BaseUrl = Base }));
            Assert.Contains("ApiKey", e1.Mensaje);
            var e2 = Assert.Throws<FactuSmartException>(() => new FactuSmartClient(new FactuSmartOptions { ApiKey = "x" }));
            Assert.Contains("BaseUrl", e2.Mensaje);
            Assert.Throws<FactuSmartException>(() => new FactuSmartClient(null!));
        }

        [Fact]
        public void Constructor_quita_la_barra_final_y_aplica_defaults()
        {
            using (var c = new FactuSmartClient(new FactuSmartOptions { ApiKey = " k ", BaseUrl = Base + "//" }))
            {
                Assert.Equal(Base, c.BaseUrl);
                Assert.Equal("k", c.ApiKey);
                Assert.Equal(TimeSpan.FromSeconds(60), c.Timeout);
                Assert.Equal(3, c.MaxReintentos);
                Assert.True(c.VerificarSsl);
                Assert.Null(c.Ruc);
            }
        }

        // ───── Cabeceras, RUC y defaults ─────

        [Fact]
        public async Task Get_manda_X_API_Key_y_ruc_en_query()
        {
            var (c, m, _) = Cliente(R(200, new { success = true, ruc_emisor = "20100070970" }));
            var r = await c.PingAsync();
            Assert.True(r.GetProperty("success").GetBoolean());
            var l = m.Llamadas.Single();
            Assert.Equal("GET", l.Metodo);
            Assert.Equal(Base + "/ping?ruc=20100070970", l.Url.ToString());
            Assert.Equal("k_prueba", l.Cabeceras["X-API-Key"]);
            Assert.StartsWith("factusmart-dotnet/", l.Cabeceras["User-Agent"]);
        }

        [Fact]
        public async Task El_ruc_por_llamada_sobreescribe_el_del_constructor()
        {
            var (c, m, _) = Cliente(R(200));
            await c.Series.ListarAsync(new OpcionesRuc { Ruc = "20999999999" });
            Assert.Contains("ruc=20999999999", m.Llamadas[0].Url.Query);
        }

        [Fact]
        public async Task Emitir_completa_defaults_ruc_en_cuerpo_y_manda_Idempotency_Key_uuid()
        {
            var (c, m, _) = Cliente(R(200, new { success = true, aceptado_por_sunat = true }));
            await c.Comprobantes.EmitirAsync(Boleta);
            var l = m.Llamadas.Single();
            Assert.Equal("POST", l.Metodo);
            Assert.Equal(Base + "/documents", l.Url.ToString());
            var b = l.Cuerpo!.Value;
            Assert.Equal("20100070970", b.Str("ruc"));
            Assert.Equal("#", b.Str("numero_documento"));
            Assert.Equal("PEN", b.Str("codigo_tipo_moneda"));
            Assert.Equal("0101", b.Str("codigo_tipo_operacion"));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", b.Str("fecha_de_emision"));
            Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", b.Str("hora_de_emision"));
            Assert.Matches(UuidV4, l.Cabeceras["Idempotency-Key"]);
            Assert.StartsWith("application/json", l.Cabeceras["Content-Type"]);
        }

        [Fact]
        public async Task Emitir_respeta_lo_que_manda_el_usuario()
        {
            var (c, m, _) = Cliente(R(200));
            await c.Comprobantes.EmitirAsync(new Dictionary<string, object>
            {
                ["codigo_tipo_documento"] = "07",
                ["ruc"] = "20111111111",
                ["numero_documento"] = "123",
                ["fecha_de_emision"] = "2026-01-02",
                ["hora_de_emision"] = "10:00:00",
                ["codigo_tipo_moneda"] = "USD",
            }, new OpcionesEmision { IdempotencyKey = "venta-1" });
            var l = m.Llamadas.Single();
            var b = l.Cuerpo!.Value;
            Assert.Equal("20111111111", b.Str("ruc"));
            Assert.Equal("123", b.Str("numero_documento"));
            Assert.Equal("2026-01-02", b.Str("fecha_de_emision"));
            Assert.Equal("10:00:00", b.Str("hora_de_emision"));
            Assert.Equal("USD", b.Str("codigo_tipo_moneda"));
            Assert.False(b.TryGetProperty("codigo_tipo_operacion", out _)); // nota: no aplica
            Assert.Equal("venta-1", l.Cabeceras["Idempotency-Key"]);
        }

        [Fact]
        public void FechaHoraPeru_es_UTC_menos_5()
        {
            var (f, h) = FactuSmartClient.FechaHoraPeru(new DateTimeOffset(2026, 1, 1, 3, 30, 0, TimeSpan.Zero));
            Assert.Equal("2025-12-31", f);
            Assert.Equal("22:30:00", h);
        }

        [Fact]
        public async Task Rucs_nunca_reciben_el_ruc_emisor_y_activar_usa_PATCH()
        {
            var (c, m, _) = Cliente(R(200), R(200), R(200));
            await c.Rucs.ListarAsync();
            await c.Rucs.CrearAsync(new { ruc = "20131312955", empresa = "X", correo = "a@b.pe", clave = "123456" });
            await c.Rucs.ActivarAsync("20131312955", false);
            Assert.Equal(Base + "/rucs", m.Llamadas[0].Url.ToString());
            Assert.False(m.Llamadas[1].Cuerpo!.Value.GetProperty("ruc").GetString() == "20100070970");
            Assert.Equal("PATCH", m.Llamadas[2].Metodo);
            Assert.Equal(Base + "/rucs/20131312955/activation", m.Llamadas[2].Url.ToString());
            Assert.False(m.Llamadas[2].Cuerpo!.Value.GetProperty("activo").GetBoolean());
            Assert.False(m.Llamadas[2].Cuerpo!.Value.TryGetProperty("ruc", out _));
        }

        [Fact]
        public async Task Listar_convierte_filtros_en_query_y_catalogos_mandan_q()
        {
            var (c, m, _) = Cliente(R(200), R(200));
            await c.Comprobantes.ListarAsync(new { estado = "05", limite = 2, antes_de = (string?)null });
            await c.Catalogos.ConsultarAsync("unidades", new OpcionesCatalogo { Q = "kilo" });
            Assert.Equal(Base + "/documents?estado=05&limite=2&ruc=20100070970", m.Llamadas[0].Url.ToString());
            Assert.Equal(Base + "/catalogos/unidades?q=kilo&ruc=20100070970", m.Llamadas[1].Url.ToString());
        }

        [Fact]
        public async Task Calcular_no_manda_ruc_y_manda_porcentaje_igv()
        {
            var (c, m, _) = Cliente(R(200, new { success = true }));
            await c.Comprobantes.CalcularAsync(new object[] { new { descripcion = "Menú", cantidad = 3, precio_unitario = 10, afectacion = "10" } },
                new OpcionesCalculo { PorcentajeIgv = 10.5m });
            var b = m.Llamadas.Single().Cuerpo!.Value;
            Assert.False(b.TryGetProperty("ruc", out _));
            Assert.Equal(10.5m, b.GetProperty("porcentaje_igv").GetDecimal());
            Assert.Equal(1, b.GetProperty("items").GetArrayLength());
            Assert.DoesNotContain("ruc=", m.Llamadas[0].Url.ToString());
        }

        [Fact]
        public async Task Reenviar_y_consultarSunat_mandan_ruc_en_el_cuerpo()
        {
            var (c, m, _) = Cliente(R(200), R(200));
            await c.Comprobantes.ReenviarAsync("abc-1");
            await c.Comprobantes.ConsultarSunatAsync("abc-1");
            Assert.Equal(Base + "/documents/abc-1/reenviar", m.Llamadas[0].Url.ToString());
            Assert.Equal("20100070970", m.Llamadas[0].Cuerpo!.Value.Str("ruc"));
            Assert.Equal(Base + "/documents/abc-1/consultar-sunat", m.Llamadas[1].Url.ToString());
        }

        [Fact]
        public async Task Descargar_devuelve_bytes_y_valida_el_tipo()
        {
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 hola");
            var (c, m, _) = Cliente(new RespuestaFalsa { Bytes = pdf });
            var r = await c.Comprobantes.DescargarAsync("abc", "pdf");
            Assert.Equal(pdf, r);
            Assert.Equal(Base + "/documents/abc/pdf?ruc=20100070970", m.Llamadas[0].Url.ToString());
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Comprobantes.DescargarAsync("abc", "zip"));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Comprobantes.ConsultarAsync(" "));
        }

        // ───── Regla de oro ─────

        [Fact]
        public async Task Http_200_con_aceptado_por_sunat_false_NO_es_excepcion()
        {
            var (c, m, _) = Cliente(R(200, new { success = true, aceptado_por_sunat = false, estado = "01" }));
            var r = await c.Comprobantes.EmitirAsync(Boleta);
            Assert.False(r.GetProperty("aceptado_por_sunat").GetBoolean());
            Assert.Single(m.Llamadas);
        }

        // ───── Errores ─────

        [Theory]
        [InlineData(401, typeof(ErrorDeAutenticacion))]
        [InlineData(402, typeof(SinCreditos))]
        [InlineData(403, typeof(RucNoAutorizado))]
        [InlineData(404, typeof(NoEncontrado))]
        [InlineData(422, typeof(ErrorDeValidacion))]
        [InlineData(429, typeof(LimiteDePeticiones))]
        [InlineData(500, typeof(FactuSmartException))]
        public async Task Cada_HTTP_lanza_su_excepcion(int status, Type tipo)
        {
            var (c, _, _) = Cliente(R(status, new { success = false, message = "algo pasó", codigo = "X_" + status, reintentable = false }));
            var e = await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.PingAsync());
            Assert.Equal(tipo, e.GetType());
            Assert.Equal(status, e.Status);
            Assert.Equal("algo pasó", e.Mensaje);
            Assert.Equal("X_" + status, e.Codigo);
            Assert.False(e.Reintentable);
            Assert.NotNull(e.Respuesta);
        }

        [Fact]
        public async Task Error_422_expone_campos_y_servidor_viejo_sin_codigo_no_rompe()
        {
            var (c, _, _) = Cliente(R(422, new
            {
                success = false, message = "Datos inválidos", codigo = "DATOS_INVALIDOS", reintentable = false,
                campos = new Dictionary<string, object> { ["items.0.cantidad"] = new[] { "Debe ser mayor a 0." } },
            }));
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c.Comprobantes.EmitirAsync(Boleta));
            Assert.Equal("DATOS_INVALIDOS", e.Codigo);
            Assert.Equal(new[] { "Debe ser mayor a 0." }, e.Campos["items.0.cantidad"]);
            Assert.NotNull(e.Errores);

            var (c2, _, _) = Cliente(R(422, new { message = new Dictionary<string, object> { ["limite"] = new[] { "El limite no debe ser mayor que 100." } } }));
            var e2 = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c2.Comprobantes.ListarAsync());
            Assert.Null(e2.Codigo);
            Assert.Null(e2.Reintentable);
            Assert.Contains("limite: El limite", e2.Mensaje);
            Assert.Empty(e2.Campos);
            Assert.NotNull(e2.Errores);
        }

        [Fact]
        public async Task Error_no_JSON_da_mensaje_por_defecto()
        {
            var (c, _, _) = Cliente(new RespuestaFalsa { Status = 404, Cuerpo = "<html>no</html>" });
            var e = await Assert.ThrowsAsync<NoEncontrado>(() => c.Comprobantes.ConsultarAsync("x"));
            Assert.Equal("Recurso no encontrado. (HTTP 404)", e.Mensaje);
            Assert.Null(e.Respuesta);
            Assert.Equal("<html>no</html>", e.RespuestaTexto);
        }

        // ───── Reintentos e idempotencia ─────

        [Fact]
        public async Task Emitir_reintenta_503_y_409_con_la_MISMA_Idempotency_Key_y_espera_creciente()
        {
            var (c, m, esperas) = Cliente(R(503), R(409, new { success = false, codigo = "EMISION_EN_CURSO", reintentable = true }), R(200, new { success = true, external_id = "e1" }));
            var r = await c.Comprobantes.EmitirAsync(Boleta);
            Assert.Equal("e1", r.Str("external_id"));
            Assert.Equal(3, m.Llamadas.Count);
            Assert.Single(m.Llamadas.Select(l => l.Cabeceras["Idempotency-Key"]).Distinct());
            Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, esperas);
        }

        [Fact]
        public async Task Limite_429_respeta_Retry_After_y_el_mensaje_Espera_N_segundos()
        {
            var r429 = R(429, new { success = false, codigo = "LIMITE_PETICIONES", reintentable = true });
            r429.Cabeceras["Retry-After"] = "7";
            var (c, m, esperas) = Cliente(r429, R(429, new { message = "Espera 12 segundos." }), R(200));
            await c.PingAsync();
            Assert.Equal(3, m.Llamadas.Count);
            Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(12) }, esperas);
        }

        [Fact]
        public async Task Limite_429_LIMITE_PRUEBAS_DIARIO_no_reintentable_lanza_tras_una_sola_peticion()
        {
            var (c, m, esperas) = Cliente(R(429, new { success = false, message = "Llegaste al tope diario de pruebas.", codigo = "LIMITE_PRUEBAS_DIARIO", reintentable = false }));
            var e = await Assert.ThrowsAsync<LimiteDePeticiones>(() => c.Comprobantes.EmitirAsync(Boleta));
            Assert.Equal("LIMITE_PRUEBAS_DIARIO", e.Codigo);
            Assert.False(e.Reintentable);
            Assert.Single(m.Llamadas);
            Assert.Empty(esperas);
        }

        [Fact]
        public async Task Reintentable_false_manda_aunque_el_HTTP_sea_503()
        {
            var (c, m, _) = Cliente(R(503, new { success = false, codigo = "X", reintentable = false }));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Cuenta.SaldoAsync());
            Assert.Single(m.Llamadas);
        }

        [Fact]
        public async Task Reintentable_true_reintenta_un_500_en_un_GET()
        {
            var (c, m, _) = Cliente(R(500, new { success = false, codigo = "ERROR_INTERNO", reintentable = true }), R(200));
            await c.Cuenta.SaldoAsync();
            Assert.Equal(2, m.Llamadas.Count);
        }

        [Fact]
        public async Task Operaciones_con_efectos_nunca_se_reintentan()
        {
            var cuerpo = new { success = false, codigo = "SUNAT_NO_DISPONIBLE", reintentable = true };
            var (c, m, _) = Cliente(R(503, cuerpo));
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Comprobantes.ReenviarAsync("a"));
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Comprobantes.ConsultarSunatAsync("a"));
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Anulaciones.CrearAsync(new { documentos = new object[0] }));
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Guias.EmitirAsync(new { codigo_tipo_documento = "09" }));
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Rucs.ActivarAsync("20131312955", true));
            Assert.Equal(5, m.Llamadas.Count);
        }

        [Fact]
        public async Task Error_409_fuera_de_emitir_no_se_reintenta()
        {
            var (c, m, _) = Cliente(R(409, new { success = false, codigo = "CONFLICTO" }));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.CrearAsync(new { ruc = "1" }));
            Assert.Single(m.Llamadas);
        }

        [Fact]
        public async Task Error_de_red_se_reintenta_y_al_final_lanza_status_0()
        {
            var red = new RespuestaFalsa { Lanzar = new HttpRequestException("conexión rechazada") };
            var (c, m, esperas) = Cliente(red);
            var e = await Assert.ThrowsAsync<FactuSmartException>(() => c.PingAsync());
            Assert.Equal(0, e.Status);
            Assert.Contains("No se pudo conectar", e.Mensaje);
            Assert.Equal(4, m.Llamadas.Count); // 1 + MaxReintentos (3)
            Assert.Equal(new[] { 1, 2, 4 }, esperas.Select(t => (int)t.TotalSeconds));
        }

        [Fact]
        public async Task Timeout_propio_lanza_FactuSmartException_y_cancelacion_del_usuario_propaga()
        {
            var lento = new ManejadorLento();
            using (var c = new FactuSmartClient(new FactuSmartOptions
            {
                ApiKey = "k", BaseUrl = Base, Timeout = TimeSpan.FromMilliseconds(50), MaxReintentos = 0, HttpClient = new HttpClient(lento),
            }))
            {
                var e = await Assert.ThrowsAsync<FactuSmartException>(() => c.PingAsync());
                Assert.Contains("no respondió", e.Mensaje);

                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
                using (var c2 = new FactuSmartClient(new FactuSmartOptions { ApiKey = "k", BaseUrl = Base, HttpClient = new HttpClient(lento) }))
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c2.PingAsync(cancellationToken: cts.Token));
            }
        }

        private sealed class ManejadorLento : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
        }

        // ───── Webhooks (6c) ─────

        [Fact]
        public void Webhook_vector_comun_y_casos_invalidos()
        {
            const string cuerpo = "{\"evento\":\"comprobante.aceptado\"}";
            const string firma = "bc1542f6420851152ae42abd2cbcfe8f651cba4a2822ddd07a15d0fe036f72da";
            Assert.True(FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma, "whsec_prueba"));
            Assert.True(FactuSmartClient.VerificarFirmaWebhook(Encoding.UTF8.GetBytes(cuerpo), firma.ToUpperInvariant(), "whsec_prueba"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma, "otro_secreto"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook("{\"evento\": \"comprobante.aceptado\"}", firma, "whsec_prueba"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook(cuerpo, "", "whsec_prueba"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma, ""));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook(cuerpo, "sha256=" + firma, "whsec_prueba"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma.Substring(2), "whsec_prueba"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook(cuerpo, new string('z', 64), "whsec_prueba"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook((string)null!, firma, "whsec_prueba"));
            Assert.False(FactuSmartClient.VerificarFirmaWebhook(cuerpo, null!, "whsec_prueba"));
        }

        // ───── calcularComprobante (9) ─────

        [Fact]
        public void CalcularComprobante_caso_comun_coincide_al_caracter_con_resultado_caso_comun_json()
        {
            var r = FactuSmartClient.CalcularComprobante(EntradaCasoComun());
            var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
            var ruta = Buscar("resultado_caso_comun.json");
            if (Environment.GetEnvironmentVariable("FACTUSMART_GENERAR_CASO_COMUN") == "1")
                File.WriteAllText(ruta, json, new UTF8Encoding(false));
            Assert.Equal(File.ReadAllText(ruta).Replace("\r\n", "\n"), json);

            var t = r.Totales;
            Assert.Equal(31.67m, t["total_operaciones_gravadas"]);
            Assert.Equal(5.7m, t["total_igv"]);
            Assert.Equal(148.77m, t["total_valor"]);
            Assert.Equal(154.47m, t["total_venta"]);
            Assert.False(r.Items[0].ContainsKey("codigo_interno"));
        }

        [Fact]
        public void CalcularComprobante_acepta_double_dictionary_y_valida()
        {
            var r = FactuSmartClient.CalcularComprobante(new object[]
            {
                new Dictionary<string, object> { ["descripcion"] = "A", ["cantidad"] = 3, ["valor_unitario"] = 10.555, ["afectacion"] = "10", ["codigo_interno"] = "P1", ["unidad_de_medida"] = "ZZ" },
            });
            Assert.Equal(31.67m, r.Items[0]["total_base_igv"]);
            Assert.Equal("P1", r.Items[0]["codigo_interno"]);
            Assert.Equal("ZZ", r.Items[0]["unidad_de_medida"]);
            Assert.Throws<ArgumentException>(() => FactuSmartClient.CalcularComprobante(new object[0]));
            Assert.Throws<ArgumentException>(() => FactuSmartClient.CalcularComprobante(new object[] { new { descripcion = "x", cantidad = 1, valor_unitario = 1, afectacion = "11" } }));
            Assert.Throws<ArgumentException>(() => FactuSmartClient.CalcularComprobante(new object[] { new { descripcion = "x", cantidad = 0, valor_unitario = 1, afectacion = "10" } }));
        }

        [Fact]
        public async Task Ejemplo_del_README_compila_y_funciona()
        {
            var (fs, m, _) = Cliente(R(200, new { aceptado_por_sunat = true, serie_numero = "F001-1" }));
            var calc = FactuSmartClient.CalcularComprobante(new[] {
                new { descripcion = "Servicio de consultoría", cantidad = 1, valor_unitario = 100m, afectacion = "10" },
            });
            var r = await fs.Comprobantes.EmitirAsync(new {
                codigo_tipo_documento = "01",
                datos_del_cliente_o_receptor = new { codigo_tipo_documento_identidad = "6", numero_documento = "20131312955" },
                items = calc.Items, totales = calc.Totales,
            });
            Assert.Equal("True F001-1", $"{r.GetProperty("aceptado_por_sunat")} {r.GetProperty("serie_numero")}");
            Assert.Equal(118m, m.Llamadas[0].Cuerpo!.Value.GetProperty("totales").GetProperty("total_venta").GetDecimal());
        }

        [Fact]
        public async Task El_resultado_de_CalcularComprobante_se_serializa_dentro_de_emitir()
        {
            var calc = FactuSmartClient.CalcularComprobante(EntradaCasoComun());
            var (c, m, _) = Cliente(R(200));
            await c.Comprobantes.EmitirAsync(new { codigo_tipo_documento = "03", items = calc.Items, totales = calc.Totales });
            var b = m.Llamadas[0].Cuerpo!.Value;
            Assert.Equal(4, b.GetProperty("items").GetArrayLength());
            Assert.Equal(154.47m, b.GetProperty("totales").GetProperty("total_venta").GetDecimal());
        }

        // ───── Establecimientos (anexos) ─────

        [Fact]
        public async Task Establecimientos_listar_GET_con_ruc_en_query_y_ruc_por_llamada()
        {
            var (c, m, _) = Cliente(R(200, new { success = true, establecimientos = new object[0] }));
            var r = await c.Establecimientos.ListarAsync();
            Assert.Equal(JsonValueKind.Array, r.GetProperty("establecimientos").ValueKind);
            await c.Establecimientos.ListarAsync(new OpcionesRuc { Ruc = "20999999999" });
            Assert.Equal("GET", m.Llamadas[0].Metodo);
            Assert.Equal(Base + "/establecimientos?ruc=20100070970", m.Llamadas[0].Url.ToString());
            Assert.Null(m.Llamadas[0].Cuerpo);
            Assert.Equal(Base + "/establecimientos?ruc=20999999999", m.Llamadas[1].Url.ToString());
        }

        [Fact]
        public async Task Establecimientos_listar_se_reintenta_como_GET()
        {
            var (c, m, _) = Cliente(R(503), R(200, new { success = true, establecimientos = new object[0] }));
            await c.Establecimientos.ListarAsync();
            Assert.Equal(2, m.Llamadas.Count);
        }

        [Fact]
        public async Task Establecimientos_crear_POST_con_datos_y_ruc_en_el_cuerpo()
        {
            var (c, m, _) = Cliente(R(201, new { success = true, message = "ok", establecimiento = new { codigo_anexo = "0001" } }));
            var r = await c.Establecimientos.CrearAsync(new
            {
                codigo_anexo = "0001", nombre = "Tienda Miraflores", direccion = "Av. Larco 345", ubigeo = "150122",
                series = new[] { new { codigo_tipo_documento = "03", serie = "B002" } },
            });
            Assert.Equal("0001", r.GetProperty("establecimiento").Str("codigo_anexo"));
            var l = m.Llamadas.Single();
            Assert.Equal("POST", l.Metodo);
            Assert.Equal(Base + "/establecimientos", l.Url.ToString());
            Assert.Equal("", l.Url.Query);
            var b = l.Cuerpo!.Value;
            Assert.Equal("20100070970", b.Str("ruc"));
            Assert.Equal("0001", b.Str("codigo_anexo"));
            Assert.Equal("Tienda Miraflores", b.Str("nombre"));
            Assert.Equal("150122", b.Str("ubigeo"));
            Assert.Equal("B002", b.GetProperty("series")[0].Str("serie"));
            Assert.False(l.Cabeceras.ContainsKey("Idempotency-Key"));
        }

        [Fact]
        public async Task Establecimientos_crear_respeta_el_ruc_de_datos_y_el_de_opciones()
        {
            var (c, m, _) = Cliente(R(201));
            await c.Establecimientos.CrearAsync(new { ruc = "20111111111", codigo_anexo = "0002" });
            await c.Establecimientos.CrearAsync(new { codigo_anexo = "0003" }, new OpcionesRuc { Ruc = "20222222222" });
            Assert.Equal("20111111111", m.Llamadas[0].Cuerpo!.Value.Str("ruc"));
            Assert.Equal("20222222222", m.Llamadas[1].Cuerpo!.Value.Str("ruc"));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Establecimientos.CrearAsync(null!));
            Assert.Equal(2, m.Llamadas.Count);
        }

        [Fact]
        public async Task Establecimientos_crear_hace_UNA_sola_peticion_ante_503()
        {
            var (c, m, esperas) = Cliente(R(503, new { success = false, codigo = "SUNAT_NO_DISPONIBLE", reintentable = true }), R(201));
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Establecimientos.CrearAsync(new { codigo_anexo = "0001" }));
            Assert.Single(m.Llamadas);
            Assert.Empty(esperas);
        }

        [Fact]
        public async Task Establecimientos_crear_limite_del_plan_es_RucNoAutorizado_LIMITE_ESTABLECIMIENTOS()
        {
            var (c, m, _) = Cliente(R(403, new { success = false, message = "Ha alcanzado el límite de 1 sucursal(es).", codigo = "LIMITE_ESTABLECIMIENTOS", reintentable = false }));
            var e = await Assert.ThrowsAsync<RucNoAutorizado>(() => c.Establecimientos.CrearAsync(new { codigo_anexo = "0001" }));
            Assert.Equal("LIMITE_ESTABLECIMIENTOS", e.Codigo);
            Assert.Single(m.Llamadas);
        }

        // ───── Código de producto SUNAT ─────

        [Fact]
        public async Task BuscarProductoSunat_GET_con_q_limite_y_ruc_en_query()
        {
            var (c, m, _) = Cliente(R(503), R(200, new { success = true, catalogo_sunat = "25", datos = new[] { new { codigo = "43211503" } } }));
            var r = await c.Catalogos.BuscarProductoSunatAsync("computador portátil", limite: 5);
            Assert.Equal("43211503", r.GetProperty("datos")[0].Str("codigo"));
            Assert.Equal(2, m.Llamadas.Count); // GET: se reintenta ante 503
            var u = m.Llamadas[1].Url;
            Assert.Equal("GET", m.Llamadas[1].Metodo);
            Assert.Equal("/api/v1/issuer/catalogos/productos-sunat", u.AbsolutePath);
            Assert.Contains("q=computador%20port%C3%A1til", u.Query);
            Assert.Contains("limite=5", u.Query);
            Assert.Contains("ruc=20100070970", u.Query);
        }

        [Fact]
        public async Task BuscarProductoSunat_sin_limite_no_lo_manda_ruc_por_llamada_y_q_vacia_lanza()
        {
            var (c, m, _) = Cliente(R(200));
            await c.Catalogos.BuscarProductoSunatAsync("4321", opciones: new OpcionesRuc { Ruc = "20999999999" });
            Assert.DoesNotContain("limite", m.Llamadas[0].Url.Query);
            Assert.Contains("ruc=20999999999", m.Llamadas[0].Url.Query);
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Catalogos.BuscarProductoSunatAsync(" "));
            Assert.Single(m.Llamadas);
        }

        [Fact]
        public async Task BuscarProductoSunat_nivel_va_en_query_solo_si_se_pasa()
        {
            var (c, m, _) = Cliente(R(200), R(200), R(200));
            await c.Catalogos.BuscarProductoSunatAsync("computador", nivel: "producto");
            await c.Catalogos.BuscarProductoSunatAsync("43", limite: 10, nivel: "familia", opciones: new OpcionesRuc { Ruc = "20999999999" });
            await c.Catalogos.BuscarProductoSunatAsync("computador", nivel: " ");
            Assert.Contains("nivel=producto", m.Llamadas[0].Url.Query);
            Assert.Contains("nivel=familia", m.Llamadas[1].Url.Query);
            Assert.Contains("limite=10", m.Llamadas[1].Url.Query);
            Assert.Contains("ruc=20999999999", m.Llamadas[1].Url.Query);
            Assert.DoesNotContain("nivel", m.Llamadas[2].Url.Query);
        }

        // ───── Descargar con formato ─────

        [Fact]
        public async Task Descargar_pdf_con_formato_manda_query_formato()
        {
            var (c, m, _) = Cliente(new RespuestaFalsa { Bytes = Encoding.ASCII.GetBytes("%PDF-1.4") });
            var pdf = await c.Comprobantes.DescargarAsync("abc", "pdf", formato: "ticket");
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            var l = m.Llamadas.Single();
            Assert.Equal("GET", l.Metodo);
            Assert.Equal("/api/v1/issuer/documents/abc/pdf", l.Url.AbsolutePath);
            Assert.Contains("formato=ticket", l.Url.Query);
            Assert.Contains("ruc=20100070970", l.Url.Query);
        }

        [Fact]
        public async Task Descargar_sin_formato_no_manda_el_parametro()
        {
            var (c, m, _) = Cliente(new RespuestaFalsa { Bytes = Encoding.ASCII.GetBytes("%PDF-1.4") });
            await c.Comprobantes.DescargarAsync("abc", "pdf");
            await c.Comprobantes.DescargarAsync("abc", "xml", new OpcionesRuc { Ruc = "20999999999" });
            Assert.DoesNotContain("formato", m.Llamadas[0].Url.Query);
            Assert.DoesNotContain("formato", m.Llamadas[1].Url.Query);
        }

        [Theory]
        [InlineData("a4")]
        [InlineData("a5")]
        [InlineData("ticket80")]
        [InlineData("ticket58")]
        [InlineData("ticket50")]
        public async Task Descargar_acepta_todos_los_formatos_validos(string formato)
        {
            var (c, m, _) = Cliente(new RespuestaFalsa { Bytes = Encoding.ASCII.GetBytes("%PDF-1.4") });
            await c.Comprobantes.DescargarAsync("abc", "pdf", formato: formato);
            Assert.Contains("formato=" + formato, m.Llamadas.Single().Url.Query);
        }

        [Theory]
        [InlineData("xml")]
        [InlineData("cdr")]
        public async Task Descargar_formato_con_xml_o_cdr_lanza_ErrorDeValidacion_sin_peticion(string tipo)
        {
            var (c, m, _) = Cliente(R(200));
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c.Comprobantes.DescargarAsync("abc", tipo, formato: "ticket"));
            Assert.Equal(422, e.Status);
            Assert.Equal("DATOS_INVALIDOS", e.Codigo);
            Assert.True(e.Campos.ContainsKey("formato"));
            Assert.Empty(m.Llamadas);
        }

        [Theory]
        [InlineData("ticket_58")]
        [InlineData("A4")]
        [InlineData("")]
        public async Task Descargar_formato_invalido_lanza_ErrorDeValidacion_sin_peticion(string formato)
        {
            var (c, m, _) = Cliente(R(200));
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c.Comprobantes.DescargarAsync("abc", "pdf", formato: formato));
            Assert.Equal(422, e.Status);
            Assert.Equal("DATOS_INVALIDOS", e.Codigo);
            Assert.False(e.Reintentable);
            Assert.True(e.Campos["formato"].Length > 0);
            Assert.Contains("ticket58", e.Mensaje);
            Assert.Empty(m.Llamadas);
        }
    }
}
