using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactuSmart
{
    /// <summary>
    /// Respuesta de <c>GET /consultas/{tipo}/{numero}</c> (<c>LookupResponse</c>): datos de un RUC o DNI.
    /// Los campos que la fuente no trae vienen en null (SUNAT directo no trae <c>estado</c> ni <c>condicion</c>;
    /// del DNI solo viene <c>nombre</c>).
    /// </summary>
    public sealed class ConsultaDocumento
    {
        [JsonPropertyName("success")] public bool Success { get; set; }

        /// <summary><c>ruc</c> o <c>dni</c>.</summary>
        [JsonPropertyName("tipo")] public string Tipo { get; set; } = "";

        /// <summary>El número consultado.</summary>
        [JsonPropertyName("numero")] public string Numero { get; set; } = "";

        /// <summary>RUC: razón social. DNI: nombres completos.</summary>
        [JsonPropertyName("nombre")] public string Nombre { get; set; } = "";

        /// <summary>Domicilio fiscal del RUC. null si la fuente no lo trae (DNI casi siempre).</summary>
        [JsonPropertyName("direccion")] public string? Direccion { get; set; }

        /// <summary>Estado del contribuyente (ej. ACTIVO). null cuando responde SUNAT directo.</summary>
        [JsonPropertyName("estado")] public string? Estado { get; set; }

        /// <summary>Condición del domicilio (ej. HABIDO). null cuando responde SUNAT directo.</summary>
        [JsonPropertyName("condicion")] public string? Condicion { get; set; }

        /// <summary>Código INEI del distrito (6 dígitos).</summary>
        [JsonPropertyName("ubigeo")] public string? Ubigeo { get; set; }

        /// <summary>true = respondido desde la caché de 30 días; no descontó del cupo.</summary>
        [JsonPropertyName("desde_cache")] public bool DesdeCache { get; set; }

        /// <summary>Consultas que te quedan en el periodo actual.</summary>
        [JsonPropertyName("consultas_restantes")] public int ConsultasRestantes { get; set; }

        /// <summary><c>mes</c> = cuenta con plan; <c>dia</c> = cuenta sin plan (50 por día).</summary>
        [JsonPropertyName("periodo")] public string Periodo { get; set; } = "";

        /// <summary>Día (yyyy-MM-dd) en que el cupo vuelve a empezar.</summary>
        [JsonPropertyName("se_renueva")] public string SeRenueva { get; set; } = "";

        /// <summary>JSON completo tal como llegó (por si el API agrega campos nuevos).</summary>
        [JsonIgnore] public JsonElement Respuesta { get; set; }
    }
}
