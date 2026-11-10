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

        // ───── Consultas RUC / DNI (lookupDocument) ─────

        private static readonly object ConsultaRuc = new
        {
            success = true, tipo = "ruc", numero = "20131312955",
            nombre = "SUPERINTENDENCIA NACIONAL DE ADUANAS Y DE ADMINISTRACION TRIBUTARIA - SUNAT",
            direccion = "AV. GARCILASO DE LA VEGA - Nro: 1472  - LIMA",
            estado = (string?)null, condicion = (string?)null, ubigeo = "150101",
            desde_cache = false, consultas_restantes = 29999, periodo = "mes", se_renueva = "2026-11-23",
        };

        [Fact]
        public async Task ConsultarRuc_hace_GET_sin_ruc_y_parsea_la_respuesta()
        {
            var (c, m, _) = Cliente(R(200, ConsultaRuc));
            var r = await c.ConsultarRucAsync(" 20131312955 ");
            var l = Assert.Single(m.Llamadas);
            Assert.Equal("GET", l.Metodo);
            Assert.Equal(Base + "/consultas/ruc/20131312955", l.Url.ToString());
            Assert.Equal("", l.Url.Query);
            Assert.False(l.Cabeceras.ContainsKey("X-RUC"));
            Assert.Null(l.Cuerpo);
            Assert.Equal("k_prueba", l.Cabeceras["X-API-Key"]);
            Assert.True(r.Success);
            Assert.Equal("ruc", r.Tipo);
            Assert.Equal("20131312955", r.Numero);
            Assert.StartsWith("SUPERINTENDENCIA", r.Nombre);
            Assert.Equal("AV. GARCILASO DE LA VEGA - Nro: 1472  - LIMA", r.Direccion);
            Assert.Null(r.Estado);
            Assert.Null(r.Condicion);
            Assert.Equal("150101", r.Ubigeo);
            Assert.False(r.DesdeCache);
            Assert.Equal(29999, r.ConsultasRestantes);
            Assert.Equal("mes", r.Periodo);
            Assert.Equal("2026-11-23", r.SeRenueva);
            Assert.Equal("ruc", r.Respuesta.Str("tipo"));
        }

        [Fact]
        public async Task ConsultarDni_y_generico_arman_la_URL_y_aceptan_nulls()
        {
            var dni = new
            {
                success = true, tipo = "dni", numero = "12345678", nombre = "JUAN CARLOS PEREZ GARCIA",
                direccion = (string?)null, estado = (string?)null, condicion = (string?)null, ubigeo = (string?)null,
                desde_cache = true, consultas_restantes = 49, periodo = "dia", se_renueva = "2026-11-07",
            };
            var (c, m, _) = Cliente(R(200, dni), R(200, dni));
            var r = await c.ConsultarDniAsync("12345678");
            await c.ConsultarDocumentoAsync("DNI", "12345678");
            Assert.All(m.Llamadas, l => Assert.Equal(Base + "/consultas/dni/12345678", l.Url.ToString()));
            Assert.Null(r.Direccion);
            Assert.Null(r.Ubigeo);
            Assert.True(r.DesdeCache);
            Assert.Equal("dia", r.Periodo);
        }

        [Fact]
        public async Task ConsultarDocumento_tipo_invalido_o_numero_vacio_falla_sin_peticion()
        {
            var (c, m, _) = Cliente(R(200, ConsultaRuc));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.ConsultarDocumentoAsync("ce", "001234567"));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.ConsultarRucAsync(" "));
            Assert.Empty(m.Llamadas);
        }

        [Fact]
        public async Task Consulta_429_LIMITE_CONSULTAS_no_se_reintenta()
        {
            var (c, m, esperas) = Cliente(R(429, new
            {
                success = false, message = "Se acabó tu cupo de consultas.", codigo = "LIMITE_CONSULTAS", reintentable = false,
                consultas_restantes = 0, limite_consultas = 60000, periodo = "mes", se_renueva = "2026-11-23",
            }), R(200, ConsultaRuc));
            var e = await Assert.ThrowsAsync<LimiteDePeticiones>(() => c.ConsultarRucAsync("20131312955"));
            Assert.Equal("LIMITE_CONSULTAS", e.Codigo);
            Assert.False(e.Reintentable);
            Assert.Equal("2026-11-23", e.Respuesta!.Value.Str("se_renueva"));
            Assert.Single(m.Llamadas);
            Assert.Empty(esperas);
        }

        [Fact]
        public async Task Consulta_429_LIMITE_PETICIONES_y_503_CONSULTA_NO_DISPONIBLE_se_reintentan()
        {
            var (c, m, _) = Cliente(
                R(429, new { success = false, codigo = "LIMITE_PETICIONES", reintentable = true, retry_after = 1 }),
                R(503, new { success = false, codigo = "CONSULTA_NO_DISPONIBLE", reintentable = true }),
                R(200, ConsultaRuc));
            var r = await c.ConsultarRucAsync("20131312955");
            Assert.Equal(3, m.Llamadas.Count);
            Assert.Equal("20131312955", r.Numero);
        }

        [Fact]
        public async Task Consulta_404_y_422_lanzan_su_excepcion_sin_reintentar()
        {
            var (c, m, _) = Cliente(R(404, new { success = false, message = "No se encontró el RUC en SUNAT.", codigo = "NO_ENCONTRADO", reintentable = false }));
            var e = await Assert.ThrowsAsync<NoEncontrado>(() => c.ConsultarRucAsync("20131312955"));
            Assert.Equal("NO_ENCONTRADO", e.Codigo);
            Assert.Single(m.Llamadas);

            var (c2, m2, _) = Cliente(R(422, new
            {
                success = false, message = "Datos inválidos: El RUC no es válido.", codigo = "DATOS_INVALIDOS", reintentable = false,
                campos = new { numero = new[] { "El RUC no es válido." } },
            }));
            var e2 = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c2.ConsultarRucAsync("20131312954"));
            Assert.Equal("DATOS_INVALIDOS", e2.Codigo);
            Assert.True(e2.Campos.ContainsKey("numero"));
            Assert.Single(m2.Llamadas);
        }

        [Fact]
        public async Task Raiz_hace_GET_a_la_base_sin_ruc()
        {
            var (c, m, _) = Cliente(R(200, new { success = true, api = "FactuSmart", docs = "https://x", estado = "ok" }));
            var r = await c.RaizAsync();
            Assert.Equal(Base + "/", Assert.Single(m.Llamadas).Url.ToString());
            Assert.Equal("ok", r.Str("estado"));
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

        // ───── Configuración del RUC ─────

        private static readonly object CertificadoOk = new
        {
            success = true, ruc = "20777666555",
            certificado = new { instalado = true, titular = "API TENANT CLEAN SAC", vence = "2027-11-10", dias_para_vencer = 364, ruc_coincide = true },
        };

        private static readonly object ConfigOk = new
        {
            success = true, ruc = "20777666555", nombre = "API TENANT CLEAN SAC", entorno = "produccion",
            certificado = new { instalado = true, vence = "2027-11-10", dias_para_vencer = 364 },
            sol = new { configurado = true, usuario = "FA*****1" },
            envio = "sunat", listo_para_produccion = true, faltantes = new string[0],
        };

        [Fact]
        public async Task SubirCertificado_desde_archivo_manda_PUT_con_base64_sin_ruc_emisor()
        {
            var contenido = new byte[] { 0x30, 0x82, 0x0A, 0x5E, 0x02, 0x01, 0x03, 0xFF, 0x00, 0x10 };
            var ruta = Path.Combine(Path.GetTempPath(), "fs-cert-" + Guid.NewGuid().ToString("N") + ".pfx");
            File.WriteAllBytes(ruta, contenido);
            try
            {
                var (c, m, _) = Cliente(R(200, CertificadoOk));
                var r = await c.Rucs.SubirCertificadoAsync("20777666555", ruta, "ClaveDelPfx");
                var l = m.Llamadas.Single();
                Assert.Equal("PUT", l.Metodo);
                Assert.Equal(Base + "/rucs/20777666555/certificado", l.Url.ToString());
                Assert.Equal(Convert.ToBase64String(contenido), l.Cuerpo!.Value.Str("certificado"));
                Assert.Equal("ClaveDelPfx", l.Cuerpo!.Value.Str("clave"));
                Assert.False(l.Cuerpo!.Value.TryGetProperty("ruc", out _));
                Assert.False(l.Cabeceras.ContainsKey("X-RUC"));
                Assert.True(r.Success);
                Assert.Equal("20777666555", r.Ruc);
                Assert.True(r.Certificado.Instalado);
                Assert.Equal("2027-11-10", r.Certificado.Vence);
                Assert.Equal(364, r.Certificado.DiasParaVencer);
                Assert.True(r.Certificado.RucCoincide);
                Assert.Null(r.Aviso);
                Assert.Equal("API TENANT CLEAN SAC", r.Respuesta.GetProperty("certificado").Str("titular"));
            }
            finally { File.Delete(ruta); }
        }

        [Fact]
        public async Task SubirCertificado_desde_bytes_y_stream_codifica_igual_y_lee_ruc_coincide_null()
        {
            var contenido = Encoding.UTF8.GetBytes("pfx-de-prueba-ñ/+=");
            var sinRuc = new
            {
                success = true, ruc = "20777666555",
                certificado = new { instalado = true, titular = "JUAN PEREZ", vence = "2027-11-10", dias_para_vencer = 364, ruc_coincide = (bool?)null },
                aviso = "No se encontró un RUC en el titular del certificado.",
            };
            var (c, m, _) = Cliente(R(200, sinRuc), R(200, sinRuc));
            var r = await c.Rucs.SubirCertificadoAsync("20777666555", contenido, "x1");
            using (var s = new MemoryStream(contenido))
                await c.Rucs.SubirCertificadoAsync("20777666555", s, "x1");
            Assert.All(m.Llamadas, l => Assert.Equal(Convert.ToBase64String(contenido), l.Cuerpo!.Value.Str("certificado")));
            Assert.Null(r.Certificado.RucCoincide);
            Assert.Equal("JUAN PEREZ", r.Certificado.Titular);
            Assert.NotNull(r.Aviso);
        }

        [Fact]
        public async Task SubirCertificado_archivo_inexistente_o_sin_clave_falla_sin_peticion_y_sin_mostrar_la_clave()
        {
            var (c, m, _) = Cliente(R(200));
            var ruta = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".pfx");
            var e = await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.SubirCertificadoAsync("20777666555", ruta, "SuperSecreta123"));
            Assert.Contains(ruta, e.Message);
            Assert.DoesNotContain("SuperSecreta123", e.ToString());
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.SubirCertificadoAsync("20777666555", new byte[] { 1 }, ""));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.SubirCertificadoAsync("20777666555", new byte[0], "x"));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.SubirCertificadoAsync(" ", new byte[] { 1 }, "x"));
            Assert.Empty(m.Llamadas);
        }

        [Fact]
        public async Task SubirCertificado_422_que_repite_la_clave_no_la_deja_en_la_excepcion()
        {
            const string clave = "Cl@ve/Secreta\"ñ";
            var contenido = new byte[] { 1, 2, 3, 4, 5, 6 };
            var b64 = Convert.ToBase64String(contenido);
            // Un servidor (o proxy) descuidado que devuelve lo que recibió.
            var eco = new
            {
                success = false,
                message = $"El certificado no abre con la clave {clave}.",
                codigo = "DATOS_INVALIDOS", reintentable = false,
                campos = new { clave = new[] { $"Clave recibida: {clave}" }, certificado = new[] { b64 } },
                recibido = new { certificado = b64, clave },
            };
            var (c, m, _) = Cliente(R(422, eco));
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c.Rucs.SubirCertificadoAsync("20777666555", contenido, clave));
            Assert.Single(m.Llamadas); // 422 no se reintenta
            Assert.Equal("DATOS_INVALIDOS", e.Codigo);
            var todo = string.Join("\n", e.Message, e.Mensaje, e.ToString(), e.RespuestaTexto, e.Respuesta?.GetRawText(),
                string.Join(" ", e.Campos.SelectMany(kv => kv.Value)));
            foreach (var secreto in new[] { clave, b64, "Cl@ve", "Secreta" })
                Assert.DoesNotContain(secreto, todo);
            Assert.Contains("***", e.Mensaje);
        }

        [Fact]
        public void Ocultar_cubre_el_escapado_de_PHP()
        {
            var texto = "{\"message\":\"clave a\\/b\\u00f1 y a/bñ y a\\/b\\u00F1\"}";
            var limpio = FactuSmartClient.Ocultar(texto, new[] { "a/bñ" });
            Assert.Equal("{\"message\":\"clave *** y *** y ***\"}", limpio);
        }

        [Fact]
        public async Task ConfigurarSunat_manda_solo_los_no_nulos_y_oculta_la_clave_SOL_en_errores()
        {
            var (c, m, _) = Cliente(R(200, ConfigOk), R(200, ConfigOk), R(422, new { success = false, message = "clave_sol ClaveSol9 inválida", codigo = "DATOS_INVALIDOS", reintentable = false }));
            var r = await c.Rucs.ConfigurarSunatAsync("20777666555", "FACTURA1", "ClaveSol9");
            await c.Rucs.ConfigurarSunatAsync("20777666555", entorno: "produccion");
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c.Rucs.ConfigurarSunatAsync("20777666555", "FACTURA1", "ClaveSol9", "produccion"));

            Assert.Equal("PUT", m.Llamadas[0].Metodo);
            Assert.Equal(Base + "/rucs/20777666555/sunat", m.Llamadas[0].Url.ToString());
            var c0 = m.Llamadas[0].Cuerpo!.Value;
            Assert.Equal(new[] { "clave_sol", "usuario_sol" }, c0.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal("FACTURA1", c0.Str("usuario_sol"));
            Assert.Equal("ClaveSol9", c0.Str("clave_sol"));
            var c1 = m.Llamadas[1].Cuerpo!.Value;
            Assert.Equal(new[] { "entorno" }, c1.EnumerateObject().Select(p => p.Name));
            Assert.Equal(3, m.Llamadas[2].Cuerpo!.Value.EnumerateObject().Count());
            Assert.All(m.Llamadas, l => Assert.False(l.Cabeceras.ContainsKey("X-RUC")));

            Assert.Equal("produccion", r.Entorno);
            Assert.True(r.Sol.Configurado);
            Assert.Equal("FA*****1", r.Sol.Usuario);
            Assert.Equal("sunat", r.Envio);
            Assert.True(r.ListoParaProduccion);
            Assert.Empty(r.Faltantes);
            Assert.DoesNotContain("ClaveSol9", e.ToString() + e.RespuestaTexto);
        }

        [Fact]
        public async Task ConfigurarSunat_valida_localmente_sin_peticion()
        {
            var (c, m, _) = Cliente(R(200, ConfigOk));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555"));
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", usuarioSol: "FACTURA1"));
            var e = await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", "FACTURA1", "ClaveSol9", "real"));
            Assert.DoesNotContain("ClaveSol9", e.ToString());
            Assert.Empty(m.Llamadas);
        }

        [Fact]
        public async Task Verificar_es_POST_sin_cuerpo_y_Configuracion_es_GET_tipados()
        {
            var verif = new
            {
                success = true, ruc = "20777666555", listo_para_emitir = false, entorno = "produccion",
                checks = new
                {
                    certificado = new { ok = (bool?)true, mensaje = "Certificado vigente." },
                    sol = new { ok = (bool?)null, mensaje = "SUNAT no respondió." },
                },
            };
            var recien = new
            {
                success = true, ruc = "20777666555", nombre = "API TENANT CLEAN SAC", entorno = "prueba",
                certificado = new { instalado = false, vence = (string?)null, dias_para_vencer = (int?)null },
                sol = new { configurado = false, usuario = "MODDATOS" },
                envio = "sunat", listo_para_produccion = false, faltantes = new[] { "certificado", "sol" },
            };
            var (c, m, _) = Cliente(R(200, verif), R(200, recien));
            var v = await c.Rucs.VerificarAsync("20777666555");
            var cfg = await c.Rucs.ConfiguracionAsync("20777666555");

            Assert.Equal("POST", m.Llamadas[0].Metodo);
            Assert.Equal(Base + "/rucs/20777666555/verificar", m.Llamadas[0].Url.ToString());
            Assert.Null(m.Llamadas[0].Cuerpo);
            Assert.Equal("GET", m.Llamadas[1].Metodo);
            Assert.Equal(Base + "/rucs/20777666555/configuracion", m.Llamadas[1].Url.ToString());
            Assert.All(m.Llamadas, l => Assert.False(l.Cabeceras.ContainsKey("X-RUC")));

            Assert.False(v.ListoParaEmitir);
            Assert.True(v.Checks.Certificado.Ok);
            Assert.Null(v.Checks.Sol.Ok);
            Assert.Equal("SUNAT no respondió.", v.Checks.Sol.Mensaje);
            Assert.Equal("prueba", cfg.Entorno);
            Assert.False(cfg.Certificado.Instalado);
            Assert.Null(cfg.Certificado.Vence);
            Assert.Null(cfg.Certificado.DiasParaVencer);
            Assert.Equal(new[] { "certificado", "sol" }, cfg.Faltantes);
        }

        [Fact]
        public async Task Verificar_y_Configuracion_se_reintentan_ante_503()
        {
            var (c, m, _) = Cliente(R(503), R(200, ConfigOk), R(503), R(200, ConfigOk));
            await c.Rucs.VerificarAsync("20777666555");
            await c.Rucs.ConfiguracionAsync("20777666555");
            Assert.Equal(4, m.Llamadas.Count);
        }

        private static readonly object ConfigPse = new
        {
            success = true, ruc = "20777666555", nombre = "API TENANT CLEAN SAC", entorno = "produccion",
            certificado = new { instalado = false, vence = (string?)null, dias_para_vencer = (int?)null },
            sol = new { configurado = false, usuario = (string?)null },
            envio = "pse", modalidad = "pse",
            pse = new { proveedor = "smartpse", configurado = true, usuario = "FU*****F" },
            listo_para_produccion = true, faltantes = new string[0],
            aviso = "Con Smart PSE el contribuyente debe designar a Smart PSE como su PSE en SUNAT con su Clave SOL.",
        };

        [Fact]
        public async Task ConfigurarSunat_PSE_manda_cuerpo_exacto_y_lee_Modalidad_Pse_y_Aviso()
        {
            var (c, m, _) = Cliente(R(200, ConfigPse));
            var r = await c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "pse", pseUsuario: "FURZQQ7F", pseClave: "379VMR6U", entorno: "produccion");

            var l = m.Llamadas.Single();
            Assert.Equal("PUT", l.Metodo);
            Assert.Equal(Base + "/rucs/20777666555/sunat", l.Url.ToString());
            var cuerpo = l.Cuerpo!.Value;
            Assert.Equal(new[] { "entorno", "modalidad", "pse_clave", "pse_usuario" }, cuerpo.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal("pse", cuerpo.Str("modalidad"));
            Assert.Equal("FURZQQ7F", cuerpo.Str("pse_usuario"));
            Assert.Equal("379VMR6U", cuerpo.Str("pse_clave"));
            Assert.Equal("produccion", cuerpo.Str("entorno"));

            Assert.Equal("pse", r.Modalidad);
            Assert.Equal("pse", r.Envio);
            Assert.NotNull(r.Pse);
            Assert.Equal("smartpse", r.Pse!.Proveedor);
            Assert.True(r.Pse.Configurado);
            Assert.Equal("FU*****F", r.Pse.Usuario);
            Assert.Contains("designar a Smart PSE", r.Aviso);
        }

        [Fact]
        public async Task ConfigurarSunat_propio_deserializa_Pse_null_y_firma_antigua_con_token_sigue_compilando()
        {
            var propio = new
            {
                success = true, ruc = "20777666555", nombre = "X", entorno = "produccion",
                certificado = new { instalado = true, vence = "2027-11-10", dias_para_vencer = 364 },
                sol = new { configurado = true, usuario = "FA*****1" },
                envio = "sunat", modalidad = "propio", pse = (object?)null, listo_para_produccion = true, faltantes = new string[0],
            };
            var (c, m, _) = Cliente(R(200, propio), R(200, ConfigOk));
            var r = await c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "propio");
            var r2 = await c.Rucs.ConfigurarSunatAsync("20777666555", "FACTURA1", "ClaveSol9", "produccion", CancellationToken.None);
            Assert.Equal(new[] { "modalidad" }, m.Llamadas[0].Cuerpo!.Value.EnumerateObject().Select(x => x.Name));
            Assert.Equal("propio", r.Modalidad);
            Assert.Null(r.Pse);
            Assert.Null(r.Aviso);
            Assert.Null(r2.Modalidad); // servidor anterior: no trae modalidad
            Assert.Equal(3, m.Llamadas[1].Cuerpo!.Value.EnumerateObject().Count());
        }

        [Fact]
        public async Task ConfigurarSunat_oculta_pse_clave_en_un_422_que_la_repite()
        {
            var (c, m, _) = Cliente(R(422, new
            {
                success = false, message = "Smart PSE rechazó la clave 379VMR6U", codigo = "DATOS_INVALIDOS", reintentable = false,
                campos = new { pse_clave = new[] { "Smart PSE rechazó 379VMR6U" } },
            }));
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "pse", pseUsuario: "FURZQQ7F", pseClave: "379VMR6U"));
            Assert.DoesNotContain("379VMR6U", e.ToString() + e.Message + e.RespuestaTexto + e.Respuesta?.GetRawText());
            Assert.Single(m.Llamadas);
        }

        [Fact]
        public async Task ConfigurarSunat_valida_PSE_y_modalidad_localmente_y_acepta_demo()
        {
            var (c, m, _) = Cliente(R(200, ConfigOk));
            var e = await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "pse", pseUsuario: "FURZQQ7F"));
            Assert.Contains("pseUsuario y pseClave", e.Message);
            var e2 = await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", pseClave: "379VMR6U"));
            Assert.DoesNotContain("379VMR6U", e2.ToString());
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "ose"));
            Assert.Empty(m.Llamadas);

            await c.Rucs.ConfigurarSunatAsync("20777666555", entorno: "demo");
            await c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "pse"); // sin credenciales: decide el API
            await c.Rucs.ConfigurarSunatAsync("20777666555", pseUsuario: "FURZQQ7F", pseClave: "379VMR6U"); // sin modalidad: decide el API
            Assert.Equal(3, m.Llamadas.Count);
            Assert.Equal("demo", m.Llamadas[0].Cuerpo!.Value.Str("entorno"));
            Assert.Equal(new[] { "modalidad" }, m.Llamadas[1].Cuerpo!.Value.EnumerateObject().Select(x => x.Name));
        }

        [Fact]
        public async Task ConfigurarSunat_y_SubirCertificado_NO_se_reintentan_ante_503()
        {
            var (c, m, esperas) = Cliente(R(503, new { success = false, codigo = "ERROR_INTERNO", reintentable = true }));
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "pse", pseUsuario: "FURZQQ7F", pseClave: "379VMR6U", entorno: "produccion"));
            Assert.Single(m.Llamadas);
            await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Rucs.SubirCertificadoAsync("20777666555", new byte[] { 1, 2, 3 }, "x1"));
            Assert.Equal(2, m.Llamadas.Count);
            Assert.Empty(esperas);
        }

        [Fact]
        public async Task Verificar_PSE_lee_Modalidad_y_Checks_Pse()
        {
            var verif = new
            {
                success = true, ruc = "20777666555", listo_para_emitir = true, entorno = "produccion", modalidad = "pse",
                checks = new
                {
                    certificado = new { ok = (bool?)null, mensaje = "Este RUC envía por PSE." },
                    sol = new { ok = (bool?)null, mensaje = "Opcional." },
                    pse = new { ok = (bool?)true, mensaje = "Smart PSE aceptó el usuario y la clave." },
                },
            };
            var sinPse = new
            {
                success = true, ruc = "20777666555", listo_para_emitir = true, entorno = "prueba", modalidad = "propio",
                checks = new { certificado = new { ok = (bool?)true, mensaje = "ok" }, sol = new { ok = (bool?)null, mensaje = "MODDATOS" } },
            };
            var (c, _, _) = Cliente(R(200, verif), R(200, sinPse));
            var v = await c.Rucs.VerificarAsync("20777666555");
            Assert.Equal("pse", v.Modalidad);
            Assert.NotNull(v.Checks.Pse);
            Assert.True(v.Checks.Pse!.Ok);
            Assert.Equal("Smart PSE aceptó el usuario y la clave.", v.Checks.Pse.Mensaje);
            Assert.Null(v.Checks.Certificado.Ok);
            var v2 = await c.Rucs.VerificarAsync("20777666555");
            Assert.Equal("propio", v2.Modalidad);
            Assert.Null(v2.Checks.Pse);
        }

        // ───── 0.5.0: confirmar y códigos nuevos de la configuración del RUC ─────

        [Fact]
        public async Task ConfigurarSunat_confirmar_solo_se_manda_cuando_es_true()
        {
            var (c, m, _) = Cliente(R(200, ConfigOk), R(200, ConfigOk), R(200, ConfigOk), R(200, ConfigOk));
            await c.Rucs.ConfigurarSunatAsync("20777666555", entorno: "prueba", confirmar: true);
            await c.Rucs.ConfigurarSunatAsync("20777666555", entorno: "prueba", confirmar: false);
            await c.Rucs.ConfigurarSunatAsync("20777666555", entorno: "prueba");
            await c.Rucs.ConfigurarSunatAsync("20777666555", "FACTURA1", "ClaveSol9", "prueba", null, null, null, CancellationToken.None); // firma 0.4.0
            Assert.Equal(new[] { "confirmar", "entorno" }, m.Llamadas[0].Cuerpo!.Value.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
            Assert.True(m.Llamadas[0].Cuerpo!.Value.GetProperty("confirmar").GetBoolean());
            Assert.Equal(new[] { "entorno" }, m.Llamadas[1].Cuerpo!.Value.EnumerateObject().Select(x => x.Name));
            Assert.Equal(new[] { "entorno" }, m.Llamadas[2].Cuerpo!.Value.EnumerateObject().Select(x => x.Name));
            Assert.Equal(3, m.Llamadas[3].Cuerpo!.Value.EnumerateObject().Count());

            // Solo confirmar no es nada que guardar: falla sin petición.
            await Assert.ThrowsAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", confirmar: true));
            Assert.Equal(4, m.Llamadas.Count);
        }

        [Fact]
        public async Task Confirmar_cambio_entorno_es_422_no_reintentable()
        {
            var (c, m, esperas) = Cliente(R(422, new
            {
                success = false,
                message = "El RUC emite en producción. Los comprobantes en prueba no tienen validez y usan la misma numeración. Repite con confirmar: true.",
                codigo = "CONFIRMAR_CAMBIO_ENTORNO", reintentable = false,
            }));
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => c.Rucs.ConfigurarSunatAsync("20777666555", entorno: "prueba"));
            Assert.Equal("CONFIRMAR_CAMBIO_ENTORNO", e.Codigo);
            Assert.False(e.Reintentable);
            Assert.Single(m.Llamadas);
            Assert.Empty(esperas);
            foreach (var cod in new[] { "LIMITE_VERIFICACIONES", "CONFIRMAR_CAMBIO_ENTORNO", "PSE_NO_DISPONIBLE", "RUC_DESACTIVADO" })
                Assert.Contains(cod, FactuSmartException.CodigosDeError);
        }

        [Fact]
        public async Task Limite_verificaciones_trae_Retry_After_y_no_se_reintenta_solo()
        {
            var r = R(429, new
            {
                success = false, message = "Hiciste más de 10 verificaciones en una hora para este RUC. Espera 1800 segundos e intenta de nuevo.",
                retry_after = 1800, codigo = "LIMITE_VERIFICACIONES", reintentable = true,
            });
            r.Cabeceras["Retry-After"] = "1800";
            var (c, m, esperas) = Cliente(r);
            var e = await Assert.ThrowsAsync<LimiteDePeticiones>(() => c.Rucs.VerificarAsync("20777666555"));
            Assert.Equal("LIMITE_VERIFICACIONES", e.Codigo);
            Assert.True(e.Reintentable);
            Assert.Equal(1800, e.SegundosDeEspera);
            Assert.Single(m.Llamadas);
            Assert.Empty(esperas);
        }

        [Fact]
        public async Task Ruc_desactivado_y_PSE_no_disponible()
        {
            var (c, m, esperas) = Cliente(
                R(403, new { success = false, message = "Este RUC está desactivado o su plan venció. Reactívalo antes de configurarlo.", codigo = "RUC_DESACTIVADO", reintentable = false }),
                R(503, new { success = false, message = "Smart PSE no respondió al comprobar las credenciales. No se guardó ningún cambio. Intenta de nuevo en unos minutos.", codigo = "PSE_NO_DISPONIBLE", reintentable = true }));
            var d = await Assert.ThrowsAsync<RucNoAutorizado>(() => c.Rucs.VerificarAsync("20777666555"));
            Assert.Equal("RUC_DESACTIVADO", d.Codigo);
            Assert.Single(m.Llamadas);

            var p = await Assert.ThrowsAnyAsync<FactuSmartException>(() => c.Rucs.ConfigurarSunatAsync("20777666555", modalidad: "pse", pseUsuario: "FURZQQ7F", pseClave: "379VMR6U", entorno: "produccion"));
            Assert.Equal(503, p.Status);
            Assert.Equal("PSE_NO_DISPONIBLE", p.Codigo);
            Assert.True(p.Reintentable);
            Assert.DoesNotContain("379VMR6U", p.ToString());
            Assert.Equal(2, m.Llamadas.Count); // el PUT no se reintenta solo
            Assert.Empty(esperas);
        }

        [Fact]
        public async Task Configuracion_lee_faltantes_nuevos_y_aviso()
        {
            var (c, _, _) = Cliente(R(200, new
            {
                success = true, ruc = "20777666555", nombre = "X", entorno = "produccion",
                certificado = new { instalado = true, vence = "2026-11-30", dias_para_vencer = 20 },
                sol = new { configurado = false, usuario = (string?)null },
                envio = "sunat", modalidad = "propio", pse = (object?)null, listo_para_produccion = false,
                faltantes = new[] { "certificado_otro_ruc", "sol" },
                aviso = "El certificado vence el 2026-11-30.",
            }));
            var r = await c.Rucs.ConfiguracionAsync("20777666555");
            Assert.Equal(new[] { Faltante.CertificadoOtroRuc, Faltante.Sol }, r.Faltantes);
            Assert.Equal("El certificado vence el 2026-11-30.", r.Aviso);
            Assert.False(r.ListoParaProduccion);
        }
    }
}
