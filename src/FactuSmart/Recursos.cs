using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FactuSmart
{
    /// <summary><c>cliente.Cuenta</c>.</summary>
    public sealed class RecursoCuenta
    {
        private readonly FactuSmartClient _c;
        internal RecursoCuenta(FactuSmartClient c) { _c = c; }

        /// <summary>GET /account — saldo (<c>creditos_disponibles</c>), plan y RUCs.</summary>
        public Task<JsonElement> SaldoAsync(CancellationToken cancellationToken = default)
            => _c.JsonAsync(new FactuSmartClient.Peticion("GET", "/account") { Reintentable = true }, cancellationToken);
    }

    /// <summary><c>cliente.Series</c>.</summary>
    public sealed class RecursoSeries
    {
        private readonly FactuSmartClient _c;
        internal RecursoSeries(FactuSmartClient c) { _c = c; }

        /// <summary>GET /series — series del RUC con el siguiente número estimado.</summary>
        public Task<JsonElement> ListarAsync(OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => _c.JsonAsync(new FactuSmartClient.Peticion("GET", "/series") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true }, cancellationToken);
    }

    /// <summary><c>cliente.Comprobantes</c>: facturas, boletas y notas.</summary>
    public sealed class RecursoComprobantes
    {
        private readonly FactuSmartClient _c;
        internal RecursoComprobantes(FactuSmartClient c) { _c = c; }

        /// <summary>
        /// POST /documents — siempre con <c>Idempotency-Key</c> (la tuya o un UUID v4), reusada en todos los
        /// reintentos. Completa <c>numero_documento</c> ("#"), fecha/hora de Perú, <c>codigo_tipo_moneda</c> (PEN)
        /// y <c>codigo_tipo_operacion</c> (0101, solo 01/03) si no vienen. Revisa <c>aceptado_por_sunat</c>.
        /// </summary>
        public async Task<JsonElement> EmitirAsync(object datos, OpcionesEmision? opciones = null, CancellationToken cancellationToken = default)
        {
            if (datos == null) throw new FactuSmartException("Comprobantes.EmitirAsync(datos): pasa el comprobante como objeto.");
            var cuerpo = _c.CuerpoConRuc(datos, opciones, "Comprobantes.EmitirAsync(datos): pasa el comprobante como objeto.");
            var (fecha, hora) = FactuSmartClient.FechaHoraPeru();
            if (Json.Falta(cuerpo, "numero_documento") || (cuerpo["numero_documento"] is JsonValue v && v.TryGetValue<string>(out var s) && s == ""))
                cuerpo["numero_documento"] = "#";
            if (Json.Falta(cuerpo, "fecha_de_emision")) cuerpo["fecha_de_emision"] = fecha;
            if (Json.Falta(cuerpo, "hora_de_emision")) cuerpo["hora_de_emision"] = hora;
            if (Json.Falta(cuerpo, "codigo_tipo_moneda")) cuerpo["codigo_tipo_moneda"] = "PEN";
            // El yaml dice que codigo_tipo_operacion "no aplica a notas": solo se completa en 01/03.
            string tipo = "";
            if (cuerpo["codigo_tipo_documento"] is JsonValue td) tipo = td.TryGetValue<string>(out var t) ? t : td.ToJsonString();
            if (Json.Falta(cuerpo, "codigo_tipo_operacion") && (tipo == "01" || tipo == "03"))
                cuerpo["codigo_tipo_operacion"] = "0101";

            var clave = string.IsNullOrWhiteSpace(opciones?.IdempotencyKey) ? Guid.NewGuid().ToString() : opciones!.IdempotencyKey!;
            var p = new FactuSmartClient.Peticion("POST", "/documents") { Cuerpo = cuerpo, Reintentable = true, ReintentarEn409 = true };
            p.Cabeceras["Idempotency-Key"] = clave;
            return await _c.JsonAsync(p, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>GET /documents — filtros: <c>estado</c>, <c>desde</c>, <c>hasta</c>, <c>limite</c>, <c>antes_de</c> (cursor).</summary>
        public async Task<JsonElement> ListarAsync(object? filtros = null, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
        {
            var p = new FactuSmartClient.Peticion("GET", "/documents") { Reintentable = true };
            foreach (var kv in Json.AObjeto(filtros, "Comprobantes.ListarAsync(filtros): pasa los filtros como objeto."))
            {
                if (kv.Value == null) continue;
                p.Query[kv.Key] = kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : kv.Value.ToJsonString();
            }
            p.Query["ruc"] = _c.RucDe(opciones);
            return await _c.JsonAsync(p, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>GET /documents/{external_id} — estado guardado (no consulta a SUNAT en vivo).</summary>
        public async Task<JsonElement> ConsultarAsync(string externalId, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => await _c.JsonAsync(new FactuSmartClient.Peticion("GET", $"/documents/{FactuSmartClient.Id(externalId)}") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true }, cancellationToken).ConfigureAwait(false);

        /// <summary>Valores de <c>formato</c> que acepta el PDF (<c>ticket</c> = <c>ticket80</c>, 80 mm).</summary>
        public static readonly IReadOnlyList<string> FormatosPdf = new[] { "a4", "a5", "ticket", "ticket80", "ticket58", "ticket50" };

        /// <summary>
        /// GET /documents/{external_id}/{pdf|xml|cdr} — devuelve los bytes del archivo.
        /// <paramref name="formato"/> (solo PDF): <c>a4</c>, <c>a5</c>, <c>ticket</c> (80 mm), <c>ticket80</c>,
        /// <c>ticket58</c>, <c>ticket50</c>. Sin formato → el A4 de siempre (no se manda el parámetro).
        /// Formato con xml/cdr o fuera de la lista → <see cref="ErrorDeValidacion"/> local, sin petición.
        /// </summary>
        public async Task<byte[]> DescargarAsync(string externalId, string tipo, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default, string? formato = null)
        {
            if (tipo != "pdf" && tipo != "xml" && tipo != "cdr")
                throw new FactuSmartException($"Tipo de archivo inválido \"{tipo}\": usa \"pdf\", \"xml\" o \"cdr\".");
            if (formato != null)
            {
                if (tipo != "pdf")
                    throw ErrorFormato($"\"formato\" solo aplica al PDF (pediste \"{tipo}\").");
                if (!FormatosPdf.Contains(formato))
                    throw ErrorFormato($"Formato de PDF inválido \"{formato}\": usa {string.Join(", ", FormatosPdf)}.");
            }
            var p = new FactuSmartClient.Peticion("GET", $"/documents/{FactuSmartClient.Id(externalId)}/{tipo}") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true };
            if (formato != null) p.Query["formato"] = formato;
            return await _c.BytesAsync(p, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>ErrorDeValidacion local (status 422, DATOS_INVALIDOS, Campos["formato"]), igual que el del API.</summary>
        private static ErrorDeValidacion ErrorFormato(string mensaje)
        {
            var cuerpo = new JsonObject
            {
                ["success"] = false,
                ["message"] = mensaje,
                ["codigo"] = "DATOS_INVALIDOS",
                ["reintentable"] = false,
                ["campos"] = new JsonObject { ["formato"] = new JsonArray(JsonValue.Create(mensaje)) },
            };
            using (var d = JsonDocument.Parse(cuerpo.ToJsonString()))
                return new ErrorDeValidacion(mensaje, 422, d.RootElement.Clone());
        }

        /// <summary>POST /documents/{external_id}/reenviar — no se reintenta automáticamente.</summary>
        public async Task<JsonElement> ReenviarAsync(string externalId, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => await _c.JsonAsync(new FactuSmartClient.Peticion("POST", $"/documents/{FactuSmartClient.Id(externalId)}/reenviar") { Cuerpo = _c.CuerpoConRuc(null, opciones, ""), Reintentable = false }, cancellationToken).ConfigureAwait(false);

        /// <summary>POST /documents/{external_id}/consultar-sunat — no se reintenta automáticamente.</summary>
        public async Task<JsonElement> ConsultarSunatAsync(string externalId, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => await _c.JsonAsync(new FactuSmartClient.Peticion("POST", $"/documents/{FactuSmartClient.Id(externalId)}/consultar-sunat") { Cuerpo = _c.CuerpoConRuc(null, opciones, ""), Reintentable = false }, cancellationToken).ConfigureAwait(false);

        /// <summary>GET /documents/{external_id}/historial.</summary>
        public async Task<JsonElement> HistorialAsync(string externalId, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => await _c.JsonAsync(new FactuSmartClient.Peticion("GET", $"/documents/{FactuSmartClient.Id(externalId)}/historial") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true }, cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// POST /calcular — FactuSmart calcula <c>items</c> y <c>totales</c> listos para emitir. No emite ni cobra
        /// crédito y no manda <c>ruc</c>. Admite <c>precio_unitario</c> (con IGV) y <c>PorcentajeIgv</c> 10.5.
        /// Sin efectos: se reintenta como un GET. Para hacerlo sin red usa <see cref="FactuSmartClient.CalcularComprobante"/>.
        /// </summary>
        public async Task<JsonElement> CalcularAsync(IEnumerable<object> items, OpcionesCalculo? opciones = null, CancellationToken cancellationToken = default)
        {
            var lista = items == null ? null : Json.ANodo(items) as JsonArray;
            if (lista == null || lista.Count == 0)
                throw new FactuSmartException("Comprobantes.CalcularAsync(items): pasa una lista con al menos un ítem.");
            var cuerpo = new JsonObject { ["items"] = lista };
            if (opciones?.PorcentajeIgv is decimal pct) cuerpo["porcentaje_igv"] = pct;
            return await _c.JsonAsync(new FactuSmartClient.Peticion("POST", "/calcular") { Cuerpo = cuerpo, Reintentable = true }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><c>cliente.Anulaciones</c>: facturas (comunicación de baja) y boletas aceptadas (resumen diario).</summary>
    public sealed class RecursoAnulaciones
    {
        private readonly FactuSmartClient _c;
        internal RecursoAnulaciones(FactuSmartClient c) { _c = c; }

        /// <summary>POST /anulaciones — no mezcles facturas y boletas (422 ANULACION_MIXTA). Nunca se reintenta.</summary>
        public async Task<JsonElement> CrearAsync(object datos, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
        {
            if (datos == null) throw new FactuSmartException("Anulaciones.CrearAsync(datos): pasa la anulación como objeto.");
            var cuerpo = _c.CuerpoConRuc(datos, opciones, "Anulaciones.CrearAsync(datos): pasa la anulación como objeto.");
            return await _c.JsonAsync(new FactuSmartClient.Peticion("POST", "/anulaciones") { Cuerpo = cuerpo, Reintentable = false }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>GET /anulaciones/{ticket}/status.</summary>
        public async Task<JsonElement> EstadoAsync(string ticket, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => await _c.JsonAsync(new FactuSmartClient.Peticion("GET", $"/anulaciones/{FactuSmartClient.Id(ticket, "el ticket de la anulación")}/status") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>cliente.Guias</c>: guías de remisión (GRE 2.0).</summary>
    public sealed class RecursoGuias
    {
        private readonly FactuSmartClient _c;
        internal RecursoGuias(FactuSmartClient c) { _c = c; }

        /// <summary>POST /guias — no usa Idempotency-Key, nunca se reintenta. Completa <c>numero_documento</c> y fecha/hora de Perú.</summary>
        public async Task<JsonElement> EmitirAsync(object datos, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
        {
            if (datos == null) throw new FactuSmartException("Guias.EmitirAsync(datos): pasa la guía como objeto.");
            var cuerpo = _c.CuerpoConRuc(datos, opciones, "Guias.EmitirAsync(datos): pasa la guía como objeto.");
            var (fecha, hora) = FactuSmartClient.FechaHoraPeru();
            if (Json.Falta(cuerpo, "numero_documento")) cuerpo["numero_documento"] = "#";
            if (Json.Falta(cuerpo, "fecha_de_emision")) cuerpo["fecha_de_emision"] = fecha;
            if (Json.Falta(cuerpo, "hora_de_emision")) cuerpo["hora_de_emision"] = hora;
            return await _c.JsonAsync(new FactuSmartClient.Peticion("POST", "/guias") { Cuerpo = cuerpo, Reintentable = false }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>GET /guias/{external_id}/status.</summary>
        public async Task<JsonElement> EstadoAsync(string externalId, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => await _c.JsonAsync(new FactuSmartClient.Peticion("GET", $"/guias/{FactuSmartClient.Id(externalId, "el external_id de la guía")}/status") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>cliente.Rucs</c>: gestión de los RUCs de la cuenta (nunca se les agrega el RUC emisor).</summary>
    public sealed class RecursoRucs
    {
        private readonly FactuSmartClient _c;
        internal RecursoRucs(FactuSmartClient c) { _c = c; }

        /// <summary>GET /rucs.</summary>
        public Task<JsonElement> ListarAsync(CancellationToken cancellationToken = default)
            => _c.JsonAsync(new FactuSmartClient.Peticion("GET", "/rucs") { Reintentable = true }, cancellationToken);

        /// <summary>POST /rucs — <c>ruc</c>, <c>empresa</c>, <c>correo</c>, <c>clave</c>. Idempotente por RUC (<c>ya_existia</c>): se reintenta.</summary>
        public async Task<JsonElement> CrearAsync(object datos, CancellationToken cancellationToken = default)
        {
            if (datos == null) throw new FactuSmartException("Rucs.CrearAsync(datos): pasa { ruc, empresa, correo, clave }.");
            var cuerpo = Json.AObjeto(datos, "Rucs.CrearAsync(datos): pasa { ruc, empresa, correo, clave }.");
            return await _c.JsonAsync(new FactuSmartClient.Peticion("POST", "/rucs") { Cuerpo = cuerpo, Reintentable = true }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>PATCH /rucs/{ruc}/activation — nunca se reintenta.</summary>
        public async Task<JsonElement> ActivarAsync(string ruc, bool activo, CancellationToken cancellationToken = default)
        {
            var id = FactuSmartClient.Id(ruc, "el RUC");
            return await _c.JsonAsync(new FactuSmartClient.Peticion("PATCH", $"/rucs/{id}/activation") { Cuerpo = new JsonObject { ["activo"] = activo }, Reintentable = false }, cancellationToken).ConfigureAwait(false);
        }

        // ───── Configuración del RUC (certificado, SOL, entorno) ─────
        // El RUC va en la ruta: nunca se manda `ruc` en el cuerpo ni en la query.
        // La clave, la clave SOL y el certificado nunca salen en una excepción (Peticion.Secretos).

        /// <summary>Entornos que acepta <c>ConfigurarSunatAsync</c> (<c>demo</c> = sinónimo de <c>prueba</c>).</summary>
        public static readonly IReadOnlyList<string> Entornos = new[] { "prueba", "produccion", "demo" };

        /// <summary>Modalidades de firma: <c>propio</c> (tu certificado, directo a SUNAT) o <c>pse</c> (Smart PSE firma y envía).</summary>
        public static readonly IReadOnlyList<string> Modalidades = new[] { "propio", "pse" };

        /// <summary>
        /// PUT /rucs/{ruc}/certificado — lee el .pfx / .p12 de <paramref name="rutaArchivo"/>, lo codifica en
        /// base64 y lo instala. El API comprueba que abra con la clave, esté vigente y sea de este RUC.
        /// Reemplaza al anterior. Cambia la configuración del RUC: no se reintenta automáticamente.
        /// </summary>
        public async Task<CertificadoSubido> SubirCertificadoAsync(string ruc, string rutaArchivo, string clave, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rutaArchivo))
                throw new FactuSmartException("Rucs.SubirCertificadoAsync(ruc, rutaArchivo, clave): falta la ruta del archivo .pfx / .p12.");
            if (!File.Exists(rutaArchivo))
                throw new FactuSmartException($"No existe el archivo del certificado: {rutaArchivo}");
            using (var archivo = new FileStream(rutaArchivo, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                return await SubirCertificadoAsync(ruc, archivo, clave, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>PUT /rucs/{ruc}/certificado — con el contenido del .pfx / .p12 que ya tienes en memoria.</summary>
        public Task<CertificadoSubido> SubirCertificadoAsync(string ruc, byte[] bytes, string clave, CancellationToken cancellationToken = default)
        {
            if (bytes == null || bytes.Length == 0)
                throw new FactuSmartException("Rucs.SubirCertificadoAsync(ruc, bytes, clave): el certificado está vacío.");
            return EnviarCertificadoAsync(ruc, Convert.ToBase64String(bytes), clave, cancellationToken);
        }

        /// <summary>PUT /rucs/{ruc}/certificado — lee el .pfx / .p12 de un <see cref="Stream"/> (no lo cierra).</summary>
        public async Task<CertificadoSubido> SubirCertificadoAsync(string ruc, Stream contenido, string clave, CancellationToken cancellationToken = default)
        {
            if (contenido == null)
                throw new FactuSmartException("Rucs.SubirCertificadoAsync(ruc, contenido, clave): pasa el Stream del .pfx / .p12.");
            using (var ms = new MemoryStream())
            {
                await contenido.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                return await SubirCertificadoAsync(ruc, ms.ToArray(), clave, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<CertificadoSubido> EnviarCertificadoAsync(string ruc, string base64, string clave, CancellationToken cancellationToken)
        {
            var id = FactuSmartClient.Id(ruc, "el RUC");
            if (string.IsNullOrEmpty(clave))
                throw new FactuSmartException("Rucs.SubirCertificadoAsync: falta la clave del certificado.");
            var p = new FactuSmartClient.Peticion("PUT", $"/rucs/{id}/certificado")
            {
                Cuerpo = new JsonObject { ["certificado"] = base64, ["clave"] = clave },
                Reintentable = false,
            };
            p.Secretos.Add(clave);
            p.Secretos.Add(base64);
            return await TipadoAsync<CertificadoSubido>(p, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// PUT /rucs/{ruc}/sunat — guarda el usuario secundario SOL y su clave (van juntos), la
        /// <paramref name="modalidad"/> de firma (<c>propio</c> | <c>pse</c>) con las credenciales CPE de
        /// Smart PSE (<paramref name="pseUsuario"/> + <paramref name="pseClave"/>, van juntos) y/o el
        /// <paramref name="entorno"/> (<c>prueba</c> | <c>produccion</c>; <c>demo</c> = <c>prueba</c>).
        /// Solo manda los que no son null. Repetir <c>modalidad: "pse"</c> sin credenciales sobre un RUC ya
        /// en PSE no cambia nada. Con <c>pse</c>, lee <see cref="ConfiguracionRuc.Aviso"/>: el contribuyente
        /// debe designar a Smart PSE como su PSE en SUNAT con su Clave SOL.
        /// No llama a SUNAT: para comprobar usa <see cref="VerificarAsync"/>. Cambia la configuración: no se reintenta.
        /// </summary>
        public async Task<ConfiguracionRuc> ConfigurarSunatAsync(string ruc, string? usuarioSol = null, string? claveSol = null, string? entorno = null,
            string? modalidad = null, string? pseUsuario = null, string? pseClave = null, CancellationToken cancellationToken = default)
        {
            var id = FactuSmartClient.Id(ruc, "el RUC");
            if (usuarioSol == null && claveSol == null && entorno == null && modalidad == null && pseUsuario == null && pseClave == null)
                throw new FactuSmartException("Rucs.ConfigurarSunatAsync: pasa usuarioSol + claveSol, entorno, modalidad (con pseUsuario + pseClave si es \"pse\"), o varios juntos.");
            if ((usuarioSol == null) != (claveSol == null))
                throw new FactuSmartException("Rucs.ConfigurarSunatAsync: usuarioSol y claveSol van juntos.");
            if ((pseUsuario == null) != (pseClave == null))
                throw new FactuSmartException("Rucs.ConfigurarSunatAsync: pseUsuario y pseClave van juntos (credenciales CPE de la empresa en Smart PSE).");
            if (entorno != null && !Entornos.Contains(entorno))
                throw new FactuSmartException($"Entorno inválido \"{entorno}\": usa \"prueba\", \"produccion\" o \"demo\" (= prueba).");
            if (modalidad != null && !Modalidades.Contains(modalidad))
                throw new FactuSmartException($"Modalidad inválida \"{modalidad}\": usa \"propio\" o \"pse\".");

            var cuerpo = new JsonObject();
            if (usuarioSol != null) cuerpo["usuario_sol"] = usuarioSol;
            if (claveSol != null) cuerpo["clave_sol"] = claveSol;
            if (entorno != null) cuerpo["entorno"] = entorno;
            if (modalidad != null) cuerpo["modalidad"] = modalidad;
            if (pseUsuario != null) cuerpo["pse_usuario"] = pseUsuario;
            if (pseClave != null) cuerpo["pse_clave"] = pseClave;
            var p = new FactuSmartClient.Peticion("PUT", $"/rucs/{id}/sunat") { Cuerpo = cuerpo, Reintentable = false };
            if (!string.IsNullOrEmpty(claveSol)) p.Secretos.Add(claveSol!);
            if (!string.IsNullOrEmpty(pseClave)) p.Secretos.Add(pseClave!);
            return await TipadoAsync<ConfiguracionRuc>(p, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Firma de la 0.3.0 (compatibilidad binaria y con llamadas posicionales que pasan el token).
        /// Equivale a la sobrecarga completa sin modalidad ni credenciales PSE.
        /// </summary>
        public Task<ConfiguracionRuc> ConfigurarSunatAsync(string ruc, string? usuarioSol, string? claveSol, string? entorno, CancellationToken cancellationToken)
            => ConfigurarSunatAsync(ruc, usuarioSol, claveSol, entorno, null, null, null, cancellationToken);

        /// <summary>
        /// POST /rucs/{ruc}/verificar — prueba el certificado y el usuario SOL contra SUNAT sin emitir nada.
        /// <c>Checks.Sol.Ok</c> null = no se pudo comprobar o no aplica (prueba, OSE/PSE, SUNAT caído).
        /// Sin efectos: se reintenta.
        /// </summary>
        public Task<VerificacionRuc> VerificarAsync(string ruc, CancellationToken cancellationToken = default)
        {
            var id = FactuSmartClient.Id(ruc, "el RUC");
            return TipadoAsync<VerificacionRuc>(new FactuSmartClient.Peticion("POST", $"/rucs/{id}/verificar") { Reintentable = true }, cancellationToken);
        }

        /// <summary>GET /rucs/{ruc}/configuracion — entorno, certificado, SOL (enmascarado), envío y <c>Faltantes</c>. No llama a SUNAT.</summary>
        public Task<ConfiguracionRuc> ConfiguracionAsync(string ruc, CancellationToken cancellationToken = default)
        {
            var id = FactuSmartClient.Id(ruc, "el RUC");
            return TipadoAsync<ConfiguracionRuc>(new FactuSmartClient.Peticion("GET", $"/rucs/{id}/configuracion") { Reintentable = true }, cancellationToken);
        }

        private async Task<T> TipadoAsync<T>(FactuSmartClient.Peticion p, CancellationToken cancellationToken) where T : RespuestaTipada
        {
            var json = await _c.JsonAsync(p, cancellationToken).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize<T>(json.GetRawText())
                ?? throw new FactuSmartException("El API respondió vacío.", 200, json);
            r.Respuesta = json;
            return r;
        }
    }

    /// <summary><c>cliente.Catalogos</c>: catálogos SUNAT. No consumen crédito.</summary>
    public sealed class RecursoCatalogos
    {
        private readonly FactuSmartClient _c;
        internal RecursoCatalogos(FactuSmartClient c) { _c = c; }

        /// <summary>GET /catalogos — <c>catalogos</c> y <c>nota</c>.</summary>
        public Task<JsonElement> ListarAsync(OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => _c.JsonAsync(new FactuSmartClient.Peticion("GET", "/catalogos") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true }, cancellationToken);

        /// <summary>GET /catalogos/{nombre} — <c>nombre</c> = "unidades" o el número SUNAT ("03"). Usa solo las filas con <c>activo: true</c>.</summary>
        public async Task<JsonElement> ConsultarAsync(string nombre, OpcionesCatalogo? opciones = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(nombre))
                throw new FactuSmartException("Catalogos.ConsultarAsync(nombre): pasa el nombre (ej. \"unidades\") o el número SUNAT (ej. \"03\").");
            var p = new FactuSmartClient.Peticion("GET", $"/catalogos/{Uri.EscapeDataString(nombre.Trim())}") { Reintentable = true };
            p.Query["q"] = opciones?.Q;
            p.Query["ruc"] = _c.RucDe(opciones);
            return await _c.JsonAsync(p, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// GET /catalogos/productos-sunat — busca el código de producto SUNAT (catálogo 25) por código exacto o
        /// prefijo, o por texto (sin tildes). <paramref name="limite"/> máx. 50. Respuesta: <c>total</c>,
        /// <c>hay_mas</c> y <c>datos</c> (<c>codigo</c>, <c>descripcion</c>…). Se reintenta como un GET.
        /// </summary>
        public async Task<JsonElement> BuscarProductoSunatAsync(string q, int? limite = null, string? nivel = null, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(q))
                throw new FactuSmartException("Catalogos.BuscarProductoSunatAsync(q): pasa un texto (ej. \"computador\") o un código (ej. \"4321\").");
            var p = new FactuSmartClient.Peticion("GET", "/catalogos/productos-sunat") { Reintentable = true };
            p.Query["q"] = q.Trim();
            p.Query["limite"] = limite?.ToString(CultureInfo.InvariantCulture);
            // segmento | familia | clase | producto. Sin nivel no se manda.
            p.Query["nivel"] = string.IsNullOrWhiteSpace(nivel) ? null : nivel.Trim();
            p.Query["ruc"] = _c.RucDe(opciones);
            return await _c.JsonAsync(p, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><c>cliente.Establecimientos</c>: locales (anexos) del RUC. No consumen crédito.</summary>
    public sealed class RecursoEstablecimientos
    {
        private readonly FactuSmartClient _c;
        internal RecursoEstablecimientos(FactuSmartClient c) { _c = c; }

        /// <summary>GET /establecimientos — <c>establecimientos</c> con su <c>codigo_anexo</c>, series y <c>predeterminado</c>.</summary>
        public Task<JsonElement> ListarAsync(OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
            => _c.JsonAsync(new FactuSmartClient.Peticion("GET", "/establecimientos") { Query = { ["ruc"] = _c.RucDe(opciones) }, Reintentable = true }, cancellationToken);

        /// <summary>
        /// POST /establecimientos — <c>codigo_anexo</c>, <c>nombre</c>, <c>direccion</c>, <c>ubigeo</c> y opcionales
        /// <c>telefono</c>, <c>email</c>, <c>crear_series_por_defecto</c>, <c>series</c>. No es idempotente: nunca se
        /// reintenta. Límite del plan → <see cref="RucNoAutorizado"/> con <c>Codigo</c> <c>LIMITE_ESTABLECIMIENTOS</c>.
        /// </summary>
        public async Task<JsonElement> CrearAsync(object datos, OpcionesRuc? opciones = null, CancellationToken cancellationToken = default)
        {
            const string ayuda = "Establecimientos.CrearAsync(datos): pasa { codigo_anexo, nombre, direccion, ubigeo }.";
            if (datos == null) throw new FactuSmartException(ayuda);
            var cuerpo = _c.CuerpoConRuc(datos, opciones, ayuda);
            return await _c.JsonAsync(new FactuSmartClient.Peticion("POST", "/establecimientos") { Cuerpo = cuerpo, Reintentable = false }, cancellationToken).ConfigureAwait(false);
        }
    }
}
