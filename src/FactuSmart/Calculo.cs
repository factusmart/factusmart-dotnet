using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FactuSmart
{
    /// <summary>Resultado de <see cref="FactuSmartClient.CalcularComprobante"/>: <c>items</c> y <c>totales</c> listos para emitir.</summary>
    public sealed class ComprobanteCalculado
    {
        [JsonPropertyName("items")]
        public List<Dictionary<string, object>> Items { get; } = new List<Dictionary<string, object>>();

        [JsonPropertyName("totales")]
        public Dictionary<string, object> Totales { get; } = new Dictionary<string, object>();
    }

    internal static class Calculo
    {
        private const decimal TasaIgv = 0.18m;
        private static readonly string[] Afectaciones = { "10", "20", "30", "40" };

        /// <summary>Redondeo a 2 decimales "mitad hacia arriba" (alejándose de cero), en decimal: sin error de coma flotante.</summary>
        internal static decimal Redondear2(decimal n) => Normalizar(Math.Round(n, 2, MidpointRounding.AwayFromZero));

        /// <summary>Quita los ceros a la derecha (5.70 → 5.7, 100.00 → 100) para que el JSON coincida con los otros SDK.</summary>
        internal static decimal Normalizar(decimal d) => d / 1.0000000000000000000000000000m;

        internal static ComprobanteCalculado Calcular(IEnumerable<object> items)
        {
            if (items == null) throw new ArgumentException("CalcularComprobante: pasa una lista con al menos un ítem.", nameof(items));

            decimal gravadas = 0, exoneradas = 0, inafectas = 0, exportacion = 0, totalIgv = 0;
            var resultado = new ComprobanteCalculado();
            var i = 0;
            foreach (var it in items)
            {
                i++;
                var obj = Json.ANodo(it) as JsonObject
                    ?? throw new ArgumentException($"CalcularComprobante: ítem {i} no es un objeto.");

                var afectacion = Texto(obj["afectacion"]);
                if (afectacion == null || Array.IndexOf(Afectaciones, afectacion) < 0)
                    throw new ArgumentException($"CalcularComprobante: ítem {i}, afectación \"{afectacion}\" no soportada (usa 10, 20, 30 o 40).");
                var cantidad = Numero(obj["cantidad"]);
                if (cantidad == null || cantidad <= 0)
                    throw new ArgumentException($"CalcularComprobante: ítem {i}, cantidad inválida.");
                var valorUnitario = Numero(obj["valor_unitario"]);
                if (valorUnitario == null || valorUnitario < 0)
                    throw new ArgumentException($"CalcularComprobante: ítem {i}, valor_unitario inválido.");

                var vu = valorUnitario.Value;
                var cant = cantidad.Value;
                var gravado = afectacion == "10";
                var baseImponible = Redondear2(vu * cant);
                var igv = gravado ? Redondear2(baseImponible * TasaIgv) : 0m;

                switch (afectacion)
                {
                    case "10": gravadas += baseImponible; break;
                    case "20": exoneradas += baseImponible; break;
                    case "30": inafectas += baseImponible; break;
                    default: exportacion += baseImponible; break;
                }
                totalIgv += igv;

                var item = new Dictionary<string, object>();
                // Sin codigo_interno se omite la clave (igual que los SDK de PHP, Node y Python).
                var codigoInterno = obj["codigo_interno"];
                if (codigoInterno != null) item["codigo_interno"] = Texto(codigoInterno) ?? codigoInterno.ToJsonString();
                item["descripcion"] = Texto(obj["descripcion"]) ?? "";
                var unidad = Texto(obj["unidad_de_medida"]);
                item["unidad_de_medida"] = string.IsNullOrEmpty(unidad) ? "NIU" : unidad!;
                item["cantidad"] = Normalizar(cant);
                item["valor_unitario"] = Redondear2(vu);
                item["codigo_tipo_precio"] = "01";
                item["precio_unitario"] = Redondear2(vu * (gravado ? 1 + TasaIgv : 1));
                item["codigo_tipo_afectacion_igv"] = afectacion;
                item["total_base_igv"] = baseImponible;
                item["porcentaje_igv"] = gravado ? 18 : 0;
                item["total_igv"] = igv;
                item["total_impuestos"] = igv;
                item["total_valor_item"] = baseImponible;
                item["total_item"] = Redondear2(baseImponible + igv);
                resultado.Items.Add(item);
            }
            if (i == 0) throw new ArgumentException("CalcularComprobante: pasa una lista con al menos un ítem.", nameof(items));

            var totalValor = Redondear2(gravadas + exoneradas + inafectas + exportacion);
            var igvTotal = Redondear2(totalIgv);
            var t = resultado.Totales;
            t["total_operaciones_gravadas"] = Redondear2(gravadas);
            t["total_operaciones_exoneradas"] = Redondear2(exoneradas);
            t["total_operaciones_inafectas"] = Redondear2(inafectas);
            t["total_exportacion"] = Redondear2(exportacion);
            t["total_igv"] = igvTotal;
            t["total_impuestos"] = igvTotal;
            t["total_valor"] = totalValor;
            t["total_venta"] = Redondear2(totalValor + igvTotal);
            return resultado;
        }

        private static string? Texto(JsonNode? n)
        {
            if (n is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString();
            }
            return null;
        }

        private static decimal? Numero(JsonNode? n)
        {
            if (!(n is JsonValue v)) return null;
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) &&
                decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) return p;
            return null;
        }
    }
}
