using System.Text.Json;
using System.Text.Json.Nodes;

namespace FactuSmart
{
    internal static class Json
    {
        /// <summary>
        /// Convierte cualquier dato del usuario (objeto anónimo, Dictionary, JsonElement, JsonNode, string JSON
        /// ya serializado no) en un JsonNode respaldado por texto, para leer números como decimal exacto.
        /// </summary>
        internal static JsonNode? ANodo(object? datos)
        {
            switch (datos)
            {
                case null:
                    return null;
                case JsonNode n:
                    return JsonNode.Parse(n.ToJsonString());
                case JsonElement e:
                    return JsonNode.Parse(e.GetRawText());
                case JsonDocument d:
                    return JsonNode.Parse(d.RootElement.GetRawText());
                default:
                    return JsonNode.Parse(JsonSerializer.Serialize(datos, datos.GetType()));
            }
        }

        internal static JsonObject AObjeto(object? datos, string error)
        {
            if (datos == null) return new JsonObject();
            return ANodo(datos) as JsonObject ?? throw new FactuSmartException(error);
        }

        /// <summary>true si la clave no existe o es null (equivale a "undefined" de los otros SDK).</summary>
        internal static bool Falta(JsonObject o, string clave) => !o.TryGetPropertyValue(clave, out var v) || v == null;
    }
}
