# Cambios

## 0.1.0 — 2026-10-14

- Primera versión (sin publicar en NuGet). Paridad con los SDK de Node 1.1.0, PHP 0.2.0 y Python 0.2.0:
  - `PingAsync`, `Cuenta.SaldoAsync`, `Series.ListarAsync`.
  - `Comprobantes`: `EmitirAsync` (siempre con `Idempotency-Key`), `ListarAsync`, `ConsultarAsync`,
    `DescargarAsync` (`byte[]`), `ReenviarAsync`, `ConsultarSunatAsync`, `HistorialAsync`, `CalcularAsync`.
  - `Anulaciones.CrearAsync/EstadoAsync` (facturas y boletas), `Guias.EmitirAsync/EstadoAsync`,
    `Rucs.ListarAsync/CrearAsync/ActivarAsync`, `Catalogos.ListarAsync/ConsultarAsync`.
  - `Comprobantes.DescargarAsync(id, "pdf", formato: "ticket")`: PDF en `a4`, `a5`, `ticket`/`ticket80`,
    `ticket58` o `ticket50` (`?formato=`; sin formato no se manda). Formato con xml/cdr o inválido →
    `ErrorDeValidacion` local (422, `DATOS_INVALIDOS`, `Campos["formato"]`) sin petición.
  - `Establecimientos.ListarAsync` (`GET /establecimientos`, se reintenta) y `Establecimientos.CrearAsync`
    (`POST /establecimientos`, `ruc` en el cuerpo, nunca se reintenta).
  - `Catalogos.BuscarProductoSunatAsync(q, limite)` (`GET /catalogos/productos-sunat`, catálogo 25).
  - Códigos `PDF_NO_GENERADO`, `LIMITE_ESTABLECIMIENTOS` y `CATALOGO_NO_DISPONIBLE` en `CodigosDeError`.
  - Estáticos `FactuSmartClient.CalcularComprobante(items)` (decimal, mismo resultado al centavo que
    los otros SDK) y `FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma, secreto)`.
  - Errores `FactuSmartException` (`Status`, `Mensaje`, `Codigo`, `Reintentable`, `Campos`, `Respuesta`)
    y subclases `ErrorDeAutenticacion`, `SinCreditos`, `RucNoAutorizado`, `NoEncontrado`,
    `ErrorDeValidacion`, `LimiteDePeticiones`.
  - Reintentos con espera creciente; `reintentable: false` manda (429 `LIMITE_PRUEBAS_DIARIO` no se reintenta).
- Targets `netstandard2.0` (.NET Framework 4.6.1+, .NET Core 2.0+, Unity, Xamarin) y `net8.0`.
  Única dependencia: `System.Text.Json` (paquete de Microsoft), solo en `netstandard2.0`.
