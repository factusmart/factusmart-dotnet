using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FactuSmart
{
    /// <summary>Opciones del cliente. <see cref="ApiKey"/> y <see cref="BaseUrl"/> son obligatorias.</summary>
    public class FactuSmartOptions
    {
        /// <summary>API key de la cuenta (header <c>X-API-Key</c>). Obligatoria.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>URL base de tu servidor, ej. <c>https://s2.factusmart.pe/api/v1/issuer</c>. Obligatoria (no hay dominio único).</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>RUC emisor por defecto. Se puede cambiar en cada llamada con <see cref="OpcionesRuc.Ruc"/>.</summary>
        public string? Ruc { get; set; }

        /// <summary>Tiempo máximo por petición. Default 60 s (SUNAT a veces tarda).</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Reintentos ante errores transitorios. Default 3.</summary>
        public int MaxReintentos { get; set; } = 3;

        /// <summary>Default true. En false acepta certificados propios (SOLO pruebas locales). Se ignora si inyectas <see cref="HttpClient"/>.</summary>
        public bool VerificarSsl { get; set; } = true;

        /// <summary>HttpClient propio (IHttpClientFactory, proxies, pruebas). Si no se pasa, el SDK crea uno.</summary>
        public HttpClient? HttpClient { get; set; }

        /// <summary>Función de espera entre reintentos. Solo para pruebas; default <c>Task.Delay</c>.</summary>
        public Func<TimeSpan, CancellationToken, Task>? Dormir { get; set; }
    }

    /// <summary>Opciones por llamada: RUC emisor (sobreescribe el del constructor).</summary>
    public class OpcionesRuc
    {
        public string? Ruc { get; set; }
    }

    /// <summary>Opciones de <c>Comprobantes.EmitirAsync</c>.</summary>
    public class OpcionesEmision : OpcionesRuc
    {
        /// <summary>Clave de idempotencia (recomendado: el id de la venta en tu sistema). Si no se pasa, se genera un UUID v4.</summary>
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>Opciones de <c>Comprobantes.CalcularAsync</c>.</summary>
    public class OpcionesCalculo
    {
        /// <summary>18 (default del API) o 10.5 (Ley 31556).</summary>
        public decimal? PorcentajeIgv { get; set; }
    }

    /// <summary>Opciones de <c>Catalogos.ConsultarAsync</c>.</summary>
    public class OpcionesCatalogo : OpcionesRuc
    {
        /// <summary>Texto a buscar dentro del catálogo.</summary>
        public string? Q { get; set; }
    }
}
