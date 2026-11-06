using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FactuSmart
{
    /// <summary>
    /// Error del API de FactuSmart (o de red / timeout). Todas las excepciones del SDK heredan de esta.
    /// <para>IMPORTANTE: un HTTP 200 con <c>aceptado_por_sunat: false</c> NO lanza excepción: es un
    /// resultado normal que tu sistema debe revisar.</para>
    /// </summary>
    public class FactuSmartException : Exception
    {
        /// <summary>Códigos de error que manda hoy el API (campo <c>codigo</c>). La lista NO es cerrada.</summary>
        public static readonly IReadOnlyList<string> CodigosDeError = new[]
        {
            "NO_AUTENTICADO", "PLAN_VENCIDO", "SIN_CREDITOS", "RUC_NO_AUTORIZADO", "RUC_DESACTIVADO",
            "LLAVE_DE_PRUEBA_EN_PRODUCCION", "SIN_PERMISO", "NO_ENCONTRADO", "RUTA_NO_ENCONTRADA",
            "METODO_NO_PERMITIDO", "EMISION_EN_CURSO", "CONFLICTO", "DATOS_INVALIDOS", "RUC_REQUERIDO",
            "RUC_SIN_TENANT", "CERTIFICADO_NO_CONFIGURADO", "ERROR_EMISION", "RECHAZADO_SUNAT",
            "REENVIO_FALLIDO", "SUNAT_NO_DISPONIBLE", "LIMITE_PETICIONES", "LIMITE_PRUEBAS_DIARIO",
            "ANULACION_MIXTA", "ERROR_INTERNO", "PDF_NO_GENERADO", "LIMITE_ESTABLECIMIENTOS", "CATALOGO_NO_DISPONIBLE",
            "LIMITE_CONSULTAS", "CONSULTA_NO_DISPONIBLE",
        };

        private static readonly IReadOnlyDictionary<string, string[]> SinCampos = new Dictionary<string, string[]>();

        /// <summary>Código HTTP (0 si fue un error de red, timeout o validación local).</summary>
        public int Status { get; }

        /// <summary>Mensaje legible del API (campo <c>message</c>) o descripción del error de red.</summary>
        public string Mensaje { get; }

        /// <summary>Código estable del error (campo <c>codigo</c>), ej. <c>NO_AUTENTICADO</c>. null si no vino del API.</summary>
        public string? Codigo { get; }

        /// <summary>Si repetir la misma petición más tarde puede funcionar (campo <c>reintentable</c>). null si el API no lo manda.</summary>
        public bool? Reintentable { get; }

        /// <summary>Errores por campo (campo <c>campos</c>, sobre todo en 422). Vacío si no hay.</summary>
        public IReadOnlyDictionary<string, string[]> Campos { get; }

        /// <summary>Cuerpo JSON completo de la respuesta de error, si era JSON.</summary>
        public JsonElement? Respuesta { get; }

        /// <summary>Cuerpo crudo de la respuesta (útil si no era JSON, p. ej. una página HTML de un proxy).</summary>
        public string? RespuestaTexto { get; }

        public FactuSmartException(string mensaje, int status = 0, JsonElement? respuesta = null, string? respuestaTexto = null, Exception? causa = null)
            : base(mensaje, causa)
        {
            Status = status;
            Mensaje = mensaje;
            Respuesta = respuesta;
            RespuestaTexto = respuestaTexto;
            Codigo = null;
            Reintentable = null;
            Campos = SinCampos;
            if (respuesta is JsonElement r && r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("codigo", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() != "")
                    Codigo = c.GetString();
                if (r.TryGetProperty("reintentable", out var re) && (re.ValueKind == JsonValueKind.True || re.ValueKind == JsonValueKind.False))
                    Reintentable = re.GetBoolean();
                if (r.TryGetProperty("campos", out var campos))
                    Campos = LeerCampos(campos) ?? SinCampos;
            }
        }

        /// <summary>Mapa campo → mensajes; acepta también un mensaje suelto por campo.</summary>
        internal static Dictionary<string, string[]>? LeerCampos(JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Object) return null;
            var salida = new Dictionary<string, string[]>();
            foreach (var p in v.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array)
                {
                    var lista = new List<string>();
                    foreach (var m in p.Value.EnumerateArray())
                        lista.Add(m.ValueKind == JsonValueKind.String ? m.GetString()! : m.GetRawText());
                    salida[p.Name] = lista.ToArray();
                }
                else if (p.Value.ValueKind == JsonValueKind.String)
                {
                    salida[p.Name] = new[] { p.Value.GetString()! };
                }
            }
            return salida;
        }
    }

    /// <summary>401: API key ausente, inválida o cuenta desactivada.</summary>
    public class ErrorDeAutenticacion : FactuSmartException
    {
        public ErrorDeAutenticacion(string mensaje, int status = 401, JsonElement? respuesta = null, string? respuestaTexto = null)
            : base(mensaje, status, respuesta, respuestaTexto) { }
    }

    /// <summary>402: la cuenta no tiene créditos. No se emitió nada.</summary>
    public class SinCreditos : FactuSmartException
    {
        public SinCreditos(string mensaje, int status = 402, JsonElement? respuesta = null, string? respuestaTexto = null)
            : base(mensaje, status, respuesta, respuestaTexto) { }
    }

    /// <summary>403: el RUC no pertenece a la cuenta, está desactivado, o llave de prueba en un RUC en producción.</summary>
    public class RucNoAutorizado : FactuSmartException
    {
        public RucNoAutorizado(string mensaje, int status = 403, JsonElement? respuesta = null, string? respuestaTexto = null)
            : base(mensaje, status, respuesta, respuestaTexto) { }
    }

    /// <summary>404: comprobante, guía, anulación, catálogo, archivo o RUC no encontrado.</summary>
    public class NoEncontrado : FactuSmartException
    {
        public NoEncontrado(string mensaje, int status = 404, JsonElement? respuesta = null, string? respuestaTexto = null)
            : base(mensaje, status, respuesta, respuestaTexto) { }
    }

    /// <summary>422: datos inválidos o error del motor de emisión. El crédito no se cobra.</summary>
    public class ErrorDeValidacion : FactuSmartException
    {
        /// <summary>Mapa campo → mensajes (formato Laravel de versiones anteriores, o <c>campos</c>). En código nuevo usa <see cref="FactuSmartException.Campos"/>.</summary>
        public IReadOnlyDictionary<string, string[]>? Errores { get; }

        public ErrorDeValidacion(string mensaje, int status = 422, JsonElement? respuesta = null, string? respuestaTexto = null, IReadOnlyDictionary<string, string[]>? errores = null)
            : base(mensaje, status, respuesta, respuestaTexto)
        {
            Errores = errores ?? (Campos.Count > 0 ? Campos : null);
        }
    }

    /// <summary>
    /// Límite superado (HTTP 429). Distingue por <see cref="FactuSmartException.Codigo"/>:
    /// <c>LIMITE_PETICIONES</c> (por minuto, el SDK reintenta) o <c>LIMITE_PRUEBAS_DIARIO</c>
    /// (tope de 200 operaciones/día en RUCs demo; NO se reintenta, se reinicia a medianoche de Lima) o
    /// <c>LIMITE_CONSULTAS</c> (cupo de consultas RUC/DNI agotado; NO se reintenta, se renueva en <c>se_renueva</c>).
    /// </summary>
    public class LimiteDePeticiones : FactuSmartException
    {
        /// <summary>Segundos que pide esperar el API (Retry-After o "Espera N segundos"), si se pudo leer.</summary>
        public int? SegundosDeEspera { get; }

        public LimiteDePeticiones(string mensaje, int status = 429, JsonElement? respuesta = null, string? respuestaTexto = null, int? segundosDeEspera = null)
            : base(mensaje, status, respuesta, respuestaTexto)
        {
            SegundosDeEspera = segundosDeEspera;
        }
    }
}
