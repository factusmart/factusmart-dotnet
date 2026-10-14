using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FactuSmart.Tests
{
    /// <summary>Respuesta programada del manejador falso.</summary>
    internal sealed class RespuestaFalsa
    {
        public int Status { get; set; } = 200;
        public object? Cuerpo { get; set; } = new { success = true };
        public byte[]? Bytes { get; set; }
        public Dictionary<string, string> Cabeceras { get; } = new Dictionary<string, string>();
        public Exception? Lanzar { get; set; }
    }

    internal sealed class LlamadaRegistrada
    {
        public string Metodo { get; set; } = "";
        public Uri Url { get; set; } = null!;
        public Dictionary<string, string> Cabeceras { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public JsonElement? Cuerpo { get; set; }
    }

    /// <summary>HttpMessageHandler falso: responde en orden la lista de respuestas y guarda cada llamada.</summary>
    internal sealed class ManejadorFalso : HttpMessageHandler
    {
        private readonly RespuestaFalsa[] _respuestas;
        public List<LlamadaRegistrada> Llamadas { get; } = new List<LlamadaRegistrada>();

        public ManejadorFalso(params RespuestaFalsa[] respuestas) { _respuestas = respuestas; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var llamada = new LlamadaRegistrada { Metodo = request.Method.Method, Url = request.RequestUri! };
            foreach (var h in request.Headers) llamada.Cabeceras[h.Key] = string.Join(",", h.Value);
            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers) llamada.Cabeceras[h.Key] = string.Join(",", h.Value);
                var texto = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var d = JsonDocument.Parse(texto)) llamada.Cuerpo = d.RootElement.Clone();
            }
            Llamadas.Add(llamada);

            var r = _respuestas[Math.Min(Llamadas.Count - 1, _respuestas.Length - 1)];
            if (r.Lanzar != null) throw r.Lanzar;
            var m = new HttpResponseMessage((HttpStatusCode)r.Status);
            if (r.Bytes != null) m.Content = new ByteArrayContent(r.Bytes);
            else
            {
                var texto = r.Cuerpo is string s ? s : JsonSerializer.Serialize(r.Cuerpo);
                m.Content = new StringContent(texto, Encoding.UTF8, "application/json");
            }
            foreach (var kv in r.Cabeceras) m.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            return m;
        }
    }

    internal static class Ayuda
    {
        public const string Base = "https://api.ejemplo.test/api/v1/issuer";

        public static (FactuSmartClient Cliente, ManejadorFalso Manejador, List<TimeSpan> Esperas) Cliente(
            params RespuestaFalsa[] respuestas)
        {
            var manejador = new ManejadorFalso(respuestas);
            var esperas = new List<TimeSpan>();
            var cliente = new FactuSmartClient(new FactuSmartOptions
            {
                ApiKey = "k_prueba",
                BaseUrl = Base + "/",
                Ruc = "20123456789",
                HttpClient = new HttpClient(manejador),
                Dormir = (t, ct) => { esperas.Add(t); return Task.CompletedTask; },
            });
            return (cliente, manejador, esperas);
        }

        public static RespuestaFalsa R(int status, object? cuerpo = null) => new RespuestaFalsa { Status = status, Cuerpo = cuerpo ?? new { success = status < 400 } };

        /// <summary>Ruta de un archivo en la raíz del repo (la carpeta que tiene FactuSmart.sln).</summary>
        public static string Buscar(string relativo)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FactuSmart.sln")))
                dir = dir.Parent;
            if (dir == null) throw new DirectoryNotFoundException("No se encontró la raíz del repo (FactuSmart.sln).");
            var ruta = Path.Combine(dir.FullName, relativo);
            if (!File.Exists(ruta)) throw new FileNotFoundException(ruta);
            return ruta;
        }

        public static IReadOnlyList<object> EntradaCasoComun() => new object[]
        {
            new { descripcion = "A", cantidad = 3, valor_unitario = 10.555m, afectacion = "10" },
            new { descripcion = "B", cantidad = 2, valor_unitario = 5, afectacion = "20" },
            new { descripcion = "C", cantidad = 1, valor_unitario = 7.1m, afectacion = "30" },
            new { descripcion = "D", cantidad = 1, valor_unitario = 100, afectacion = "40" },
        };

        /// <summary>Igualdad profunda de JSON, comparando números como decimal (5.7 == 5.70).</summary>
        public static bool JsonIgual(JsonElement a, JsonElement b, string ruta, List<string> diferencias)
        {
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            {
                if (a.GetDecimal() != b.GetDecimal()) diferencias.Add($"{ruta}: {a} ≠ {b}");
                return diferencias.Count == 0;
            }
            if (a.ValueKind != b.ValueKind) { diferencias.Add($"{ruta}: {a.ValueKind} ≠ {b.ValueKind}"); return false; }
            switch (a.ValueKind)
            {
                case JsonValueKind.Object:
                    var ka = a.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var kb = b.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (!ka.SequenceEqual(kb)) diferencias.Add($"{ruta}: claves [{string.Join(",", ka)}] ≠ [{string.Join(",", kb)}]");
                    foreach (var k in ka.Intersect(kb)) JsonIgual(a.GetProperty(k), b.GetProperty(k), $"{ruta}.{k}", diferencias);
                    break;
                case JsonValueKind.Array:
                    if (a.GetArrayLength() != b.GetArrayLength()) { diferencias.Add($"{ruta}: largo distinto"); break; }
                    for (var i = 0; i < a.GetArrayLength(); i++) JsonIgual(a[i], b[i], $"{ruta}[{i}]", diferencias);
                    break;
                default:
                    if (a.GetRawText() != b.GetRawText()) diferencias.Add($"{ruta}: {a.GetRawText()} ≠ {b.GetRawText()}");
                    break;
            }
            return diferencias.Count == 0;
        }

        public static JsonElement Parse(string texto)
        {
            using (var d = JsonDocument.Parse(texto)) return d.RootElement.Clone();
        }

        public static string Str(this JsonElement e, string prop) => e.GetProperty(prop).GetString()!;
    }
}
