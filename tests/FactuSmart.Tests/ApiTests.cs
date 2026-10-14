using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using static FactuSmart.Tests.Ayuda;

namespace FactuSmart.Tests
{
    /// <summary>
    /// Cliente y boleta compartidos por las pruebas contra el API local (nunca producción).
    /// Emite como máximo 3 comprobantes por corrida (boleta, idempotencia y boleta con /calcular): en RUCs
    /// demo no gastan crédito, pero hay un tope de 200 operaciones/día por cuenta compartido con los otros
    /// SDK (429 LIMITE_PRUEBAS_DIARIO).
    /// Variables opcionales: FACTUSMART_BASE_URL, FACTUSMART_API_KEY, FACTUSMART_RUC.
    /// </summary>
    public sealed class ApiLocal : IDisposable
    {
        public string BaseUrl { get; } = Environment.GetEnvironmentVariable("FACTUSMART_BASE_URL") ?? "https://factusmart.test/api/v1/issuer";
        public string ApiKey { get; } = Environment.GetEnvironmentVariable("FACTUSMART_API_KEY") ?? "test_apikey_demo_123";
        public string Ruc { get; } = Environment.GetEnvironmentVariable("FACTUSMART_RUC") ?? "20777666555";
        public FactuSmartClient Cliente { get; }
        private readonly Lazy<Task<JsonElement>> _boleta;

        public ApiLocal()
        {
            if (BaseUrl.IndexOf("factusmart.pe", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Estas pruebas no se corren contra producción.");
            Cliente = Nuevo();
            _boleta = new Lazy<Task<JsonElement>>(() => Cliente.Comprobantes.EmitirAsync(Boleta("Prueba SDK .NET - boleta")));
        }

        // verificarSsl=false SOLO aquí: https://factusmart.test usa un certificado propio.
        public FactuSmartClient Nuevo(string? apiKey = null) => new FactuSmartClient(new FactuSmartOptions
        {
            ApiKey = apiKey ?? ApiKey, BaseUrl = BaseUrl, Ruc = Ruc, VerificarSsl = false,
        });

        /// <summary>La boleta de la prueba 3 (se emite una sola vez por corrida).</summary>
        public Task<JsonElement> BoletaEmitida => _boleta.Value;

        public static object Cliente03 => new
        {
            codigo_tipo_documento_identidad = "1",
            numero_documento = "12345678",
            apellidos_y_nombres_o_razon_social = "CLIENTE PRUEBA SDK DOTNET",
        };

        /// <summary>Boleta pequeña a un cliente con DNI (nombre enviado para no depender de RENIEC).</summary>
        public static object Boleta(string descripcion)
        {
            var calc = FactuSmartClient.CalcularComprobante(new object[]
            {
                new { codigo_interno = "SDK-NET-01", descripcion, cantidad = 2, valor_unitario = 12.5m, afectacion = "10" },
            });
            return new { codigo_tipo_documento = "03", datos_del_cliente_o_receptor = Cliente03, items = calc.Items, totales = calc.Totales };
        }

        public void Dispose() => Cliente.Dispose();
    }

    [Trait("Categoria", "Integracion")]
    public class ApiTests : IClassFixture<ApiLocal>
    {
        private readonly ApiLocal _api;
        private readonly ITestOutputHelper _salida;
        private FactuSmartClient Fs => _api.Cliente;

        public ApiTests(ApiLocal api, ITestOutputHelper salida) { _api = api; _salida = salida; }

        [Fact]
        public async Task P1_ping_responde()
        {
            var r = await Fs.PingAsync();
            Assert.True(r.GetProperty("success").GetBoolean());
            Assert.Equal(_api.Ruc, r.Str("ruc_emisor"));
            Assert.Equal(JsonValueKind.Number, r.GetProperty("creditos").ValueKind);
        }

        [Fact]
        public async Task P2_cuenta_saldo_trae_creditos_disponibles()
        {
            var r = await Fs.Cuenta.SaldoAsync();
            Assert.True(r.GetProperty("success").GetBoolean());
            Assert.True(r.GetProperty("creditos_disponibles").GetDecimal() > 0, "la cuenta local no tiene créditos");
        }

        [Fact]
        public async Task P3_emitir_boleta_con_CalcularComprobante_aceptada_y_en_entorno_prueba()
        {
            var r = await _api.BoletaEmitida;
            Assert.True(r.GetProperty("success").GetBoolean());
            Assert.Matches(@"^B\w{3}-\d+$", r.Str("serie_numero"));
            Assert.Equal("prueba", r.Str("entorno"));
            Assert.False(r.GetProperty("tiene_validez_fiscal").GetBoolean());
            Assert.True(r.GetProperty("aceptado_por_sunat").GetBoolean(), $"SUNAT beta no aceptó: {r}");
            _salida.WriteLine($"boleta {r.Str("serie_numero")} external_id {r.Str("external_id")}");
        }

        [Fact]
        public async Task P4_consultar_historial_y_descargar_pdf()
        {
            var emitido = await _api.BoletaEmitida;
            var id = emitido.Str("external_id");
            var c = await Fs.Comprobantes.ConsultarAsync(id);
            Assert.Equal(id, c.Str("external_id"));
            Assert.Equal(emitido.Str("serie_numero"), c.Str("serie_numero"));
            Assert.False(string.IsNullOrEmpty(c.GetProperty("links").Str("pdf")));

            var h = await Fs.Comprobantes.HistorialAsync(id);
            Assert.Equal(id, h.Str("external_id"));
            Assert.True(h.GetProperty("historial").GetArrayLength() > 0);

            var pdf = await Fs.Comprobantes.DescargarAsync(id, "pdf");
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            _salida.WriteLine($"PDF de {pdf.Length} bytes");
        }

        [Fact]
        public async Task P5_idempotencia_misma_clave_mismo_external_id_y_la_segunda_no_cobra()
        {
            // En RUCs demo el saldo no baja: se compara credito_cobrado (si viene), no el saldo.
            var clave = "sdk-dotnet-" + Guid.NewGuid();
            var datos = ApiLocal.Boleta("Prueba SDK .NET - idempotencia");
            var r1 = await Fs.Comprobantes.EmitirAsync(datos, new OpcionesEmision { IdempotencyKey = clave });
            var r2 = await Fs.Comprobantes.EmitirAsync(datos, new OpcionesEmision { IdempotencyKey = clave });
            Assert.Equal(r1.Str("external_id"), r2.Str("external_id"));
            Assert.Equal(r1.Str("serie_numero"), r2.Str("serie_numero"));
            if (r2.TryGetProperty("credito_cobrado", out var cc)) Assert.False(cc.GetBoolean(), $"la segunda llamada cobró: {r2}");
            _salida.WriteLine($"credito_cobrado 1ª {Prop(r1, "credito_cobrado")}, 2ª {Prop(r2, "credito_cobrado")}; modo_demo {Prop(r1, "modo_demo")}, pruebas_restantes_hoy {Prop(r2, "pruebas_restantes_hoy")}");
        }

        [Fact]
        public async Task P5b_calcular_en_servidor_igual_a_resultado_caso_comun_y_boleta_con_su_resultado()
        {
            var esperado = Parse(System.IO.File.ReadAllText(Buscar("resultado_caso_comun.json")));
            var r = await Fs.Comprobantes.CalcularAsync(EntradaCasoComun());
            Assert.True(r.GetProperty("success").GetBoolean());
            var dif = new List<string>();
            JsonIgual(r.GetProperty("items"), esperado.GetProperty("items"), "items", dif);
            JsonIgual(r.GetProperty("totales"), esperado.GetProperty("totales"), "totales", dif);
            Assert.True(dif.Count == 0, string.Join("\n", dif));

            // Precio CON IGV calculado por el servidor (3 × S/ 10.00 = S/ 30.00).
            var c = await Fs.Comprobantes.CalcularAsync(new object[]
            {
                new { codigo_interno = "SDK-NET-02", descripcion = "Prueba SDK .NET - calcular", cantidad = 3, precio_unitario = 10, afectacion = "10", unidad_de_medida = "ZZ" },
            });
            Assert.Equal(30m, c.GetProperty("totales").GetProperty("total_venta").GetDecimal());
            var e = await Fs.Comprobantes.EmitirAsync(new
            {
                codigo_tipo_documento = "03", datos_del_cliente_o_receptor = ApiLocal.Cliente03,
                items = c.GetProperty("items"), totales = c.GetProperty("totales"),
            });
            Assert.True(e.GetProperty("aceptado_por_sunat").GetBoolean(), $"SUNAT beta no aceptó: {e}");
            Assert.Equal("prueba", e.Str("entorno"));
            if (e.TryGetProperty("credito_cobrado", out var cc)) Assert.False(cc.GetBoolean(), "un RUC demo no debe cobrar crédito");
        }

        [Fact]
        public async Task P5c_catalogos_listar_y_consultar_07_trae_codigo_10()
        {
            var l = await Fs.Catalogos.ListarAsync();
            Assert.True(l.GetProperty("success").GetBoolean());
            Assert.Contains(l.GetProperty("catalogos").EnumerateArray(), x => x.Str("nombre") == "afectacion-igv");

            var c = await Fs.Catalogos.ConsultarAsync("07");
            Assert.Equal("07", c.Str("catalogo_sunat"));
            var fila = c.GetProperty("datos").EnumerateArray().FirstOrDefault(x => x.Str("codigo") == "10");
            Assert.Equal(JsonValueKind.Object, fila.ValueKind);
            Assert.True(fila.GetProperty("activo").ValueKind == JsonValueKind.True || fila.GetProperty("activo").ValueKind == JsonValueKind.False);

            var u = await Fs.Catalogos.ConsultarAsync("unidades", new OpcionesCatalogo { Q = "NIU" });
            Assert.Contains(u.GetProperty("datos").EnumerateArray(), x => x.Str("codigo") == "NIU");

            var e = await Assert.ThrowsAsync<NoEncontrado>(() => Fs.Catalogos.ConsultarAsync("no-existe-sdk"));
            Assert.Equal("NO_ENCONTRADO", e.Codigo);
        }

        [Fact]
        public async Task P6_apiKey_falsa_lanza_ErrorDeAutenticacion_NO_AUTENTICADO()
        {
            using (var falso = _api.Nuevo("llave_falsa_que_no_existe"))
            {
                var e = await Assert.ThrowsAsync<ErrorDeAutenticacion>(() => falso.PingAsync());
                Assert.Equal(401, e.Status);
                Assert.Equal("NO_AUTENTICADO", e.Codigo);
                Assert.False(e.Reintentable);
            }
        }

        [Fact]
        public async Task P6b_descargar_inexistente_lanza_NoEncontrado_NO_ENCONTRADO()
        {
            var e = await Assert.ThrowsAsync<NoEncontrado>(() => Fs.Comprobantes.DescargarAsync("00000000-0000-4000-8000-000000000000", "pdf"));
            Assert.Equal(404, e.Status);
            Assert.Equal("NO_ENCONTRADO", e.Codigo);
            Assert.False(e.Reintentable);
            Assert.DoesNotContain("<html", e.Mensaje, StringComparison.OrdinalIgnoreCase);
            _salida.WriteLine($"mensaje del API (descarga): {e.Mensaje}");
        }

        [Fact]
        public async Task P7_payload_invalido_lanza_ErrorDeValidacion_con_mensaje_legible()
        {
            var datos = JsonSerializer.SerializeToNode(ApiLocal.Boleta("Prueba SDK .NET - inválida"))!.AsObject();
            datos["serie_documento"] = "ZZ99";
            var e = await Assert.ThrowsAsync<ErrorDeValidacion>(() => Fs.Comprobantes.EmitirAsync(datos));
            Assert.Equal(422, e.Status);
            Assert.True(e.Mensaje.Length > 10, e.Mensaje);
            _salida.WriteLine($"mensaje del API: {e.Mensaje}");

            var e2 = await Assert.ThrowsAsync<ErrorDeValidacion>(() => Fs.Comprobantes.ListarAsync(new { limite = 500 }));
            Assert.Contains("limite", e2.Mensaje, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("DATOS_INVALIDOS", e2.Codigo);
            Assert.True(e2.Campos.ContainsKey("limite") && e2.Campos["limite"].Length > 0);
            _salida.WriteLine($"mensaje del API (listar): {e2.Mensaje}");
        }

        [Fact]
        public async Task Extra_series_comprobantes_y_rucs_listar_responden()
        {
            var s = await Fs.Series.ListarAsync();
            Assert.Contains(s.GetProperty("series").EnumerateArray(), x => x.Str("codigo_tipo_documento") == "03");
            var l = await Fs.Comprobantes.ListarAsync(new { limite = 2 });
            Assert.True(l.GetProperty("comprobantes").GetArrayLength() <= 2);
            var r = await Fs.Rucs.ListarAsync();
            Assert.Contains(r.GetProperty("rucs").EnumerateArray(), x => x.Str("ruc") == _api.Ruc);
        }

        private static string Prop(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetRawText() : "(no viene)";
    }
}

namespace FactuSmart.Tests
{
    /// <summary>
    /// Pruebas contra el API local que NO emiten ni crean nada (solo lectura): anexos, PDF en ticket de un
    /// comprobante ya existente y código de producto SUNAT. Se corren aparte con
    /// <c>--filter Categoria=IntegracionLectura</c>. <c>Establecimientos.CrearAsync</c> solo se prueba sin red.
    /// </summary>
    [Trait("Categoria", "IntegracionLectura")]
    public class ApiLecturaTests : IClassFixture<ApiLocal>
    {
        private readonly ApiLocal _api;
        private readonly ITestOutputHelper _salida;
        private FactuSmartClient Fs => _api.Cliente;

        public ApiLecturaTests(ApiLocal api, ITestOutputHelper salida) { _api = api; _salida = salida; }

        [Fact]
        public async Task P10_establecimientos_listar_trae_establecimientos()
        {
            var r = await Fs.Establecimientos.ListarAsync();
            Assert.True(r.GetProperty("success").GetBoolean());
            var lista = r.GetProperty("establecimientos");
            Assert.Equal(JsonValueKind.Array, lista.ValueKind);
            Assert.True(lista.GetArrayLength() > 0, "el RUC local no tiene locales");
            Assert.False(string.IsNullOrEmpty(lista[0].Str("codigo_anexo")));
            _salida.WriteLine($"{lista.GetArrayLength()} local(es); primero {lista[0].Str("codigo_anexo")} {lista[0].Str("nombre")}");
        }

        [Fact]
        public async Task P11_descargar_pdf_ticket58_de_un_comprobante_existente()
        {
            var l = await Fs.Comprobantes.ListarAsync(new { limite = 1 });
            var comprobantes = l.GetProperty("comprobantes");
            Assert.True(comprobantes.GetArrayLength() > 0, "el RUC local no tiene comprobantes para descargar");
            var id = comprobantes[0].Str("external_id");
            var pdf = await Fs.Comprobantes.DescargarAsync(id, "pdf", formato: "ticket58");
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            _salida.WriteLine($"PDF ticket58 de {comprobantes[0].Str("serie_numero")}: {pdf.Length} bytes");
        }

        [Fact]
        public async Task P12_buscar_producto_sunat_computador_trae_43211503()
        {
            var r = await Fs.Catalogos.BuscarProductoSunatAsync("computador");
            Assert.True(r.GetProperty("success").GetBoolean());
            Assert.Contains(r.GetProperty("datos").EnumerateArray(), x => x.Str("codigo") == "43211503");
            var c = await Fs.Catalogos.BuscarProductoSunatAsync("computador", limite: 1);
            Assert.True(c.GetProperty("datos").GetArrayLength() <= 1);
        }
    }
}
