# Mapeo del Sistema e Infraestructura - Sercom Soporte

Este documento describe la arquitectura de red, los servidores, los puertos, el flujo de datos y las políticas de seguridad del sistema de soporte remoto.

---

## 1. Mapeo de Servidores y Puertos

El bot y el backend de Express que actúan como puente del sistema de soporte corren de forma centralizada en el servidor **SV1** de producción:

* **Servidor SV1:** `sv1.sercommx.com`
* **Puerto de Soporte (Express):** `6001` (abierto en el firewall `iptables` de SV1 de forma global).
* **Puerto de SSH del Servidor:** `7365` (usado para la administración y sincronización de código).

---

## 2. API Endpoints (Express en Puerto 6001)

### A. Registro del Cliente (`POST /soporte/register`)
* **Uso:** El agente C# se registra al encender enviando su ID, hostname y JSON de salud.
* **Cabecera requerida:** `X-Sercom-Agent-Token: <SU_TOKEN_DE_AGENTE_SEGURO>`
* **Cuerpo (JSON):** `{ "id": "XXXX-XXXX", "hostname": "PC-ArqRodolfo", "health": { ... } }`

### B. Sondeo de Comandos (`GET /soporte/poll`)
* **Uso:** El agente consulta cada 1 segundo si el técnico ha encolado un comando.
* **Cabecera requerida:** `X-Sercom-Agent-Token: <SU_TOKEN_DE_AGENTE_SEGURO>`
* **Query Params:** `?id=XXXX-XXXX`

### C. Retorno de Salida de Consola (`POST /soporte/response`)
* **Uso:** El agente C# devuelve el output de texto de PowerShell tras ejecutar el comando.
* **Cabecera requerida:** `X-Sercom-Agent-Token: <SU_TOKEN_DE_AGENTE_SEGURO>`
* **Cuerpo (JSON):** `{ "id": "XXXX-XXXX", "cmdId": "cmd_XXXX", "output": "..." }`

### D. Inyección Externa de Comandos (`POST /soporte/cmd`)
* **Uso:** Permite a scripts o herramientas externas mandar comandos a los clientes de forma remota.
* **Cabecera requerida:** `X-Sercom-API-Key: <SU_API_KEY_DE_SOPORTE_SEGURO>`
* **Cuerpo (JSON):** `{ "id": "XXXX-XXXX", "cmd": "Get-Process" }`

### E. Descargas de Recursos Dinámicos:
* **`/soporte/download/gui-src` (GET):** Entrega el código fuente C# (`SoporteRemotoGUI.cs`) inyectando dinámicamente en caliente las imágenes en Base64 desde el disco.
* **`/soporte/download/favicon` (GET):** Descarga el archivo de icono `favicon.ico` al escritorio del cliente para compilar el ejecutable.

---

## 3. Integración con el Bot de WhatsApp (Mismo Proceso)

El bot de WhatsApp (`index.js` en SV1) corre en el mismo proceso de Node.js que el Express de soporte. Esto permite una comunicación directa en memoria:

1. El técnico escribe en WhatsApp: `.alex soporte cmd 9809-4887 Get-Process`.
2. El bot intercepta el mensaje, valida el JID del técnico y escribe directamente el comando en la cola (`queue`) en memoria de la sesión activa del Express.
3. El cliente C# recoge el comando en su siguiente sondeo, lo ejecuta y lo responde.
4. El bot recibe el output e imprime en WhatsApp la respuesta de consola formateada.
