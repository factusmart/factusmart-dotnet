# Cambios

## 0.1.0 — 2026-10-14

- Primera versión (sin publicar en NuGet). Paridad con los SDK de Node 1.1.0, PHP 0.2.0 y Python 0.2.0:
  - `PingAsync`, `Cuenta.SaldoAsync`, `Series.ListarAsync`.
  - `Comprobantes`: `EmitirAsync` (siempre con `Idempotency-Key`), `ListarAsync`, `ConsultarAsync`,
    `DescargarAsync` (`byte[]`), `ReenviarAsync`, `ConsultarSunatAsync`, `HistorialAsync`, `CalcularAsync`.
  - `Anulaciones.CrearAsync/EstadoAsync` (facturas y boletas), `Guias.EmitirAsync/EstadoAsync`,
    `Rucs.ListarAsync/CrearAsync/ActivarAsync`, `Catalogos.ListarAsync/ConsultarAsync`.
  - Estáticos `FactuSmartClient.CalcularComprobante(items)` (decimal, mismo resultado al centavo que
    los otros SDK) y `FactuSmartClient.VerificarFirmaWebhook(cuerpo, firma, secreto)`.
  - Errores `FactuSmartException` (`Status`, `Mensaje`, `Codigo`, `Reintentable`, `Campos`, `Respuesta`)
    y subclases `ErrorDeAutenticacion`, `SinCreditos`, `RucNoAutorizado`, `NoEncontrado`,
    `ErrorDeValidacion`, `LimiteDePeticiones`.
  - Reintentos con espera creciente; `reintentable: false` manda (429 `LIMITE_PRUEBAS_DIARIO` no se reintenta).
- Targets `netstandard2.0` (.NET Framework 4.6.1+, .NET Core 2.0+, Unity, Xamarin) y `net8.0`.
  Única dependencia: `System.Text.Json` (paquete de Microsoft), solo en `netstandard2.0`.
