# SercomDesk — Sistema de Soporte Remoto Propietario

Sistema de soporte técnico remoto desarrollado por **Sercom Mx**. Elimina la dependencia de AnyDesk y RustDesk con una solución 100% propia, integrada con el bot de WhatsApp corporativo.

---

## 📁 Estructura del Proyecto

```
sercom-soporte/
├── client/
│   └── SoporteRemotoGUI.cs      # Agente Windows en C# (.NET 4.8)
├── server/
│   ├── relay.js                  # Servidor WebSocket relay (puerto 6002)
│   └── package.json              # Dependencias Node.js (ws@8)
├── panel/
│   └── index.html               # Panel web del técnico (HTML5 Canvas)
├── agent_spec.md                # Especificación técnica del agente
├── system_mapping.md            # Mapeo de infraestructura y API endpoints
└── project_roadmap.md           # Roadmap y fases de desarrollo
```

---

## 🚀 Fases Implementadas

| Fase | Descripción | Estado |
|------|-------------|--------|
| 1 | Consola remota PowerShell + Health Check via WhatsApp | ✅ Completado |
| 2 | Sincronización de portapapeles bidireccional | ✅ Completado |
| 3 | Streaming de pantalla en tiempo real (Canvas WebSocket) | ✅ Completado |
| 4 | Control de mouse, teclado y elevación UAC a demanda | ✅ Completado |

---

## 🏗️ Cómo Compilar el Agente (en Windows del cliente)

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe `
  /out:SoporteRemotoGUI.exe `
  /target:winexe `
  /win32icon:favicon.ico `
  SoporteRemotoGUI.cs
```

---

## 🖥️ Cómo Desplegar el Relay (en SV1)

```bash
cd /home/alex/alex_omega/whatsapp_sovereign/
npm install ws
node relay.js
```

---

## 🔐 Claves de Autenticación

> [!IMPORTANT]
> Los secretos y tokens de autenticación no deben hardcodearse en el repositorio. Deben definirse mediante variables de entorno o archivos de configuración locales.

| Clave | Uso | Valor Recomendado |
|-------|-----|-------------------|
| `X-Sercom-Agent-Token` | Header del agente C# | *Definido en configuración local / Variables de entorno del servidor* |
| `X-Sercom-API-Key` | API Key para Panel/Scripts | *Definido en configuración local / Variables de entorno del servidor* |
| `panel_key` | Query param del Panel Web | *Definido en configuración local / Variables de entorno del servidor* |

---

## 📡 Puertos de Infraestructura (SV1)

| Puerto | Servicio |
|--------|---------|
| `6001` | API REST (Express + Bot WhatsApp) |
| `6002` | WebSocket Relay (Streaming en tiempo real) |

---

**Sercom Mx © 2026** — Todos los derechos reservados.
