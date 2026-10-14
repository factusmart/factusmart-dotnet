using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FactuSmart
{
    /// <summary>
    /// Cliente del API de facturación electrónica de FactuSmart.
    /// <para>Regla de oro: un HTTP 200 con <c>aceptado_por_sunat: false</c> NO es excepción; revisa la respuesta.</para>
    /// </summary>
    public sealed class FactuSmartClient : IDisposable
    {
        public const string Version = "0.1.0";

        private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(60);
        private static readonly Regex FirmaHex = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex EsperaEnMensaje = new Regex(@"espera\s+(\d+)\s*segundo", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex TooManyAttempts = new Regex("too many attempts", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly HttpClient _http;
        private readonly bool _httpPropio;
        private readonly Func<TimeSpan, CancellationToken, Task> _dormir;

        public string ApiKey { get; }
        public string BaseUrl { get; }
        public string? Ruc { get; }
        public TimeSpan Timeout { get; }
        public int MaxReintentos { get; }
        public bool VerificarSsl { get; }

        /// <summary>GET /account.</summary>
        public RecursoCuenta Cuenta { get; }
        /// <summary>GET /series.</summary>
        public RecursoSeries Series { get; }
        /// <summary>/documents y /calcular.</summary>
        public RecursoComprobantes Comprobantes { get; }
        /// <summary>/anulaciones.</summary>
        public RecursoAnulaciones Anulaciones { get; }
        /// <summary>/guias.</summary>
        public RecursoGuias Guias { get; }
        /// <summary>/rucs.</summary>
        public RecursoRucs Rucs { get; }
        /// <summary>/catalogos.</summary>
        public RecursoCatalogos Catalogos { get; }

        public FactuSmartClient(FactuSmartOptions opciones)
        {
            if (opciones == null)
                throw new FactuSmartException("Pasa las opciones del cliente: new FactuSmartClient(new FactuSmartOptions { ApiKey, BaseUrl, Ruc }).");
            if (string.IsNullOrWhiteSpace(opciones.ApiKey))
                throw new FactuSmartException("Falta ApiKey: es la API key de tu panel de integrador (pestaña Configuración).");
            if (string.IsNullOrWhiteSpace(opciones.BaseUrl))
                throw new FactuSmartException("Falta BaseUrl: copia la \"URL base de la API\" de tu panel de integrador (ej. https://s2.factusmart.pe/api/v1/issuer).");
            if (opciones.Timeout <= TimeSpan.Zero)
                throw new FactuSmartException("Timeout debe ser mayor que cero.");

            ApiKey = opciones.ApiKey.Trim();
            BaseUrl = opciones.BaseUrl.Trim().TrimEnd('/');
            Ruc = string.IsNullOrWhiteSpace(opciones.Ruc) ? null : opciones.Ruc!.Trim();
            Timeout = opciones.Timeout;
            MaxReintentos = Math.Max(0, opciones.MaxReintentos);
            VerificarSsl = opciones.VerificarSsl;
            _dormir = opciones.Dormir ?? ((t, ct) => Task.Delay(t, ct));

            if (opciones.HttpClient != null)
            {
                _http = opciones.HttpClient;
                _httpPropio = false;
            }
            else
            {
                var manejador = new HttpClientHandler();
                if (!VerificarSsl)
                    manejador.ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true;
                _http = new HttpClient(manejador) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                _httpPropio = true;
            }

            Cuenta = new RecursoCuenta(this);
            Series = new RecursoSeries(this);
            Comprobantes = new RecursoComprobantes(this);
            Anulaciones = new RecursoAnulaciones(this);
            Guias = new RecursoGuias(this);
            Rucs = new RecursoRucs(this);
            Catalogos = new RecursoCatalogos(this);
        }

        public void Dispose()
        {
            if (_httpPropio) _http.Dispose();
        }

        // ───────────── Estáticos ─────────────

        /// <summary>
        /// Calcula <c>items</c> y <c>totales</c> listos para <c>Comprobantes.EmitirAsync</c>, sin red.
        /// Cada ítem: <c>descripcion</c>, <c>cantidad</c>, <c>valor_unitario</c> (sin IGV), <c>afectacion</c>
        /// (10 gravado 18 %, 20 exonerado, 30 inafecto, 40 exportación) y opcional <c>codigo_interno</c>,
        /// <c>unidad_de_medida</c> (default NIU). Acepta objetos anónimos, Dictionary o JsonElement.
        /// </summary>
        public static ComprobanteCalculado CalcularComprobante(IEnumerable<object> items) => Calculo.Calcular(items);

        /// <summary>
        /// Verifica el header <c>X-FactuSmart-Firma</c> de un webhook: HMAC-SHA256 (hex minúscula, sin prefijo)
        /// del cuerpo CRUDO con tu secreto. Comparación en tiempo constante. Nunca lanza: ante datos vacíos o
        /// mal formados devuelve false.
        /// </summary>
        public static bool VerificarFirmaWebhook(string cuerpoCrudo, string firma, string secreto)
        {
            if (cuerpoCrudo == null) return false;
            return VerificarFirmaWebhook(Encoding.UTF8.GetBytes(cuerpoCrudo), firma, secreto);
        }

        /// <summary>Igual que la sobrecarga de string, con el cuerpo en bytes tal como llegó.</summary>
        public static bool VerificarFirmaWebhook(byte[] cuerpoCrudo, string firma, string secreto)
        {
            try
            {
                if (cuerpoCrudo == null || firma == null || string.IsNullOrEmpty(secreto)) return false;
                var recibida = firma.Trim().ToLowerInvariant();
                if (!FirmaHex.IsMatch(recibida)) return false;
                byte[] esperada;
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secreto)))
                    esperada = hmac.ComputeHash(cuerpoCrudo);
                var dada = new byte[32];
                for (var i = 0; i < 32; i++)
                    dada[i] = Convert.ToByte(recibida.Substring(i * 2, 2), 16);
                return IgualesEnTiempoConstante(dada, esperada);
            }
            catch
            {
                return false;
            }
        }

        private static bool IgualesEnTiempoConstante(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var diferencia = 0;
            for (var i = 0; i < a.Length; i++) diferencia |= a[i] ^ b[i];
            return diferencia == 0;
        }

        /// <summary>Fecha (yyyy-MM-dd) y hora (HH:mm:ss) actuales en Perú (UTC-5 fijo; Perú no usa horario de verano).</summary>
        public static (string Fecha, string Hora) FechaHoraPeru(DateTimeOffset? ahora = null)
        {
            var lima = (ahora ?? DateTimeOffset.UtcNow).ToUniversalTime().AddHours(-5);
            return (lima.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), lima.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }

        // ───────────── Métodos ─────────────

        /// <summary>GET /ping — prueba la API key. No consume crédito.</summary>
        public Task<JsonElement> PingAsync(OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => JsonAsync(new Peticion("GET", "/ping") { Query = { ["ruc"] = RucDe(opciones) }, Reintentable = true }, cancellationToken);

        // ───────────── Internos ─────────────

        internal string? RucDe(OpcionesRuc? o) => string.IsNullOrWhiteSpace(o?.Ruc) ? Ruc : o!.Ruc!.Trim();

        /// <summary>En POST el RUC va en el cuerpo; si el usuario ya puso <c>ruc</c> en sus datos, se respeta el suyo.</summary>
        internal JsonObject CuerpoConRuc(object? datos, OpcionesRuc? o, string error)
        {
            var cuerpo = Json.AObjeto(datos, error);
            var ruc = RucDe(o);
            if (Json.Falta(cuerpo, "ruc") && ruc != null) cuerpo["ruc"] = ruc;
            return cuerpo;
        }

        internal static string Id(string externalId, string que = "el external_id del comprobante")
        {
            if (string.IsNullOrWhiteSpace(externalId)) throw new FactuSmartException($"Falta {que}.");
            return Uri.EscapeDataString(externalId.Trim());
        }

        internal sealed class Peticion
        {
            public Peticion(string metodo, string ruta) { Metodo = metodo; Ruta = ruta; }
            public string Metodo { get; }
            public string Ruta { get; }
            public Dictionary<string, string?> Query { get; } = new Dictionary<string, string?>();
            public JsonNode? Cuerpo { get; set; }
            public Dictionary<string, string> Cabeceras { get; } = new Dictionary<string, string>();
            /// <summary>Si se puede reintentar ante red/timeout/429/502/503/504.</summary>
            public bool Reintentable { get; set; }
            /// <summary>Reintentar también ante 409 (solo emitir con Idempotency-Key).</summary>
            public bool ReintentarEn409 { get; set; }
            public bool Binario { get; set; }
        }

        internal async Task<JsonElement> JsonAsync(Peticion p, CancellationToken ct)
        {
            var (status, bytes) = await EnviarAsync(p, ct).ConfigureAwait(false);
            try
            {
                using (var doc = JsonDocument.Parse(bytes))
                    return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new FactuSmartException($"El API respondió algo que no es JSON (HTTP {status}).", status, null, Encoding.UTF8.GetString(bytes));
            }
        }

        internal async Task<byte[]> BytesAsync(Peticion p, CancellationToken ct)
        {
            p.Binario = true;
            var (_, bytes) = await EnviarAsync(p, ct).ConfigureAwait(false);
            return bytes;
        }

        private string ConstruirUrl(Peticion p)
        {
            var sb = new StringBuilder(BaseUrl).Append(p.Ruta);
            var primero = true;
            foreach (var kv in p.Query)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                sb.Append(primero ? '?' : '&').Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
                primero = false;
            }
            return sb.ToString();
        }

        private HttpRequestMessage ConstruirMensaje(Peticion p, string url, string? cuerpoTexto)
        {
            var m = new HttpRequestMessage(new HttpMethod(p.Metodo), url);
            m.Headers.TryAddWithoutValidation("X-API-Key", ApiKey);
            m.Headers.TryAddWithoutValidation("Accept", p.Binario
                ? "application/pdf, application/xml, application/zip, application/json;q=0.9, */*;q=0.8"
                : "application/json");
            m.Headers.TryAddWithoutValidation("User-Agent", $"factusmart-dotnet/{Version}");
            foreach (var kv in p.Cabeceras) m.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (cuerpoTexto != null)
                m.Content = new StringContent(cuerpoTexto, Encoding.UTF8, "application/json");
            return m;
        }

        private async Task<(int Status, byte[] Cuerpo)> EnviarAsync(Peticion p, CancellationToken ct)
        {
            var url = ConstruirUrl(p);
            var cuerpoTexto = p.Cuerpo?.ToJsonString();
            var maxIntentos = p.Reintentable ? MaxReintentos + 1 : 1;

            for (var intento = 0; ; intento++)
            {
                var quedan = intento + 1 < maxIntentos;
                Task Esperar(int? segundos)
                {
                    var t = segundos.HasValue ? TimeSpan.FromSeconds(segundos.Value) : TimeSpan.FromSeconds(Math.Pow(2, intento));
                    return _dormir(t > EsperaMaxima ? EsperaMaxima : t, ct);
                }

                using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limite.CancelAfter(Timeout);
                    HttpResponseMessage respuesta;
                    byte[] bytes;
                    try
                    {
                        using (var mensaje = ConstruirMensaje(p, url, cuerpoTexto))
                        {
                            respuesta = await _http.SendAsync(mensaje, HttpCompletionOption.ResponseContentRead, limite.Token).ConfigureAwait(false);
                        }
                        using (respuesta)
                        {
#if NET5_0_OR_GREATER
                            bytes = await respuesta.Content.ReadAsByteArrayAsync(limite.Token).ConfigureAwait(false);
#else
                            bytes = await respuesta.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
                            if (respuesta.IsSuccessStatusCode)
                                return ((int)respuesta.StatusCode, bytes);

                            var error = ConstruirError(respuesta, bytes);
                            var status = (int)respuesta.StatusCode;
                            // `reintentable: false` en el cuerpo manda sobre la regla por HTTP (ej. 429 LIMITE_PRUEBAS_DIARIO).
                            var transitorio = error.Reintentable != false && (
                                error is LimiteDePeticiones || status == 502 || status == 503 || status == 504 ||
                                (status == 409 && p.ReintentarEn409) ||
                                // Señal adicional del API; solo cuenta en operaciones reintentables (en las demás maxIntentos es 1).
                                error.Reintentable == true);
                            if (transitorio && quedan)
                            {
                                await Esperar((error as LimiteDePeticiones)?.SegundosDeEspera).ConfigureAwait(false);
                                continue;
                            }
                            throw error;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout propio (no fue el CancellationToken del usuario).
                        if (quedan) { await Esperar(null).ConfigureAwait(false); continue; }
                        throw new FactuSmartException($"El API no respondió en {Timeout.TotalSeconds:0.##} s ({p.Metodo} {p.Ruta}).");
                    }
                    catch (HttpRequestException e)
                    {
                        if (quedan) { await Esperar(null).ConfigureAwait(false); continue; }
                        var causa = e.InnerException?.Message ?? e.Message;
                        throw new FactuSmartException($"No se pudo conectar con el API ({p.Metodo} {p.Ruta}): {causa}", 0, null, null, e);
                    }
                }
            }
        }

        private static FactuSmartException ConstruirError(HttpResponseMessage respuesta, byte[] bytes)
        {
            var status = (int)respuesta.StatusCode;
            var texto = Encoding.UTF8.GetString(bytes);
            JsonElement? cuerpo = null;
            try
            {
                using (var doc = JsonDocument.Parse(bytes)) cuerpo = doc.RootElement.Clone();
            }
            catch (JsonException) { /* no es JSON (p. ej. HTML de un proxy) */ }

            var mensaje = "";
            IReadOnlyDictionary<string, string[]>? errores = null;
            if (cuerpo is JsonElement c && c.ValueKind == JsonValueKind.Object)
            {
                JsonElement m;
                var tieneM = c.TryGetProperty("message", out m) || c.TryGetProperty("mensaje", out m);
                if (tieneM && m.ValueKind == JsonValueKind.String) mensaje = m.GetString() ?? "";
                else if (tieneM && EsMapaDeValidacion(m)) { errores = FactuSmartException.LeerCampos(m); mensaje = MapaATexto(errores!); }
                else if (EsMapaDeValidacion(c)) { errores = FactuSmartException.LeerCampos(c); mensaje = MapaATexto(errores!); }
            }
            if (mensaje == "")
            {
                string baseTexto;
                switch (status)
                {
                    case 401: baseTexto = "API key inválida o ausente."; break;
                    case 402: baseTexto = "Sin créditos disponibles en la cuenta."; break;
                    case 403: baseTexto = "El RUC no está autorizado para esta cuenta."; break;
                    case 404: baseTexto = "Recurso no encontrado."; break;
                    case 422: baseTexto = "Datos inválidos."; break;
                    case 429: baseTexto = "Límite de peticiones alcanzado."; break;
                    default: baseTexto = "Error del API de FactuSmart."; break;
                }
                mensaje = $"{baseTexto} (HTTP {status})";
            }

            string? codigo = null;
            if (cuerpo is JsonElement cc && cc.ValueKind == JsonValueKind.Object && cc.TryGetProperty("codigo", out var cod) && cod.ValueKind == JsonValueKind.String)
                codigo = cod.GetString();
            // Servidores anteriores convertían el 429 del limitador en un 500 "Too Many Attempts.".
            var esLimite = status == 429 || codigo == "LIMITE_PETICIONES" || (status == 500 && TooManyAttempts.IsMatch(mensaje));
            if (esLimite)
                return new LimiteDePeticiones(mensaje, status, cuerpo, texto, LeerSegundosDeEspera(respuesta.Headers.RetryAfter, mensaje));
            switch (status)
            {
                case 401: return new ErrorDeAutenticacion(mensaje, status, cuerpo, texto);
                case 402: return new SinCreditos(mensaje, status, cuerpo, texto);
                case 403: return new RucNoAutorizado(mensaje, status, cuerpo, texto);
                case 404: return new NoEncontrado(mensaje, status, cuerpo, texto);
                case 422: return new ErrorDeValidacion(mensaje, status, cuerpo, texto, errores);
                default: return new FactuSmartException(mensaje, status, cuerpo, texto);
            }
        }

        private static bool EsMapaDeValidacion(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Object) return false;
            var hay = false;
            foreach (var p in v.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) return false;
                hay = true;
            }
            return hay;
        }

        private static string MapaATexto(IReadOnlyDictionary<string, string[]> mapa)
            => string.Join(" | ", mapa.Select(kv => $"{kv.Key}: {string.Join(" ", kv.Value)}"));

        private static int? LeerSegundosDeEspera(RetryConditionHeaderValue? retryAfter, string mensaje)
        {
            if (retryAfter?.Delta is TimeSpan d && d >= TimeSpan.Zero) return (int)Math.Ceiling(d.TotalSeconds);
            if (retryAfter?.Date is DateTimeOffset f) return Math.Max(0, (int)Math.Ceiling((f - DateTimeOffset.UtcNow).TotalSeconds));
            var m = EsperaEnMensaje.Match(mensaje);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        }
    }
}
