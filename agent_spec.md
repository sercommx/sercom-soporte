# Especificación Técnica del Agente - Sercom Soporte

Este documento detalla el diseño, la lógica de negocio y las funciones del cliente de soporte remoto desarrollado en **C# (.NET Framework 4.8)** para el entorno de Windows de los clientes.

---

## 1. Diseño de la Interfaz (WinForms)

La ventana gráfica del cliente está diseñada para ser minimalista, limpia y corporativa (estilo Fluent de Windows 11), eliminando la consola negra CMD de fondo usando la bandera de compilación `/target:winexe`.

* **Fondo de Ventana:** Blanco Puro (`#FFFFFF`) para una sensación limpia y SaaS.
* **Header de Marca:** Logotipo de **Sercom Mx** (inyectado dinámicamente en caliente por Express en Base64) centrado arriba.
* **Contenedor del ID de Soporte:** Un control `Panel` gris claro (`#F3F4F6`) con un borde gris suave de `1px` (`#E5E7EB`) que enmarca la etiqueta del ID.
* **Código de Soporte:** Texto grande en negrita (`Segoe UI Semibold`, `26pt`) en azul oscuro institucional (`#0A2540`).
* **Botón de Cierre:** Botón plano (`FlatStyle.Flat`) azul oscuro que cambia a azul cobalto en Hover, con el puntero del mouse en modo mano (`Cursors.Hand`) para mejorar el UX.

---

## 2. Generación del ID Persistente (8 Dígitos)

El agente calcula un ID estable de 8 dígitos con guión (formato `XXXX-XXXX`) de forma determinista para que el cliente siempre tenga el mismo ID asignado:

1. Intenta leer el **Número de Serie físico de la Placa Madre** ejecutando en segundo plano de forma silenciosa: `wmic baseboard get serialnumber`.
2. Si falla o devuelve un valor genérico (ej: "To be filled by O.E.M."), hace un fallback al **Número de Serie de la BIOS** (`wmic bios get serialnumber`).
3. Si ambos fallan, usa el `Hostname` de la máquina.
4. Calcula un hash matemático estable sumando los caracteres del serial y realiza operaciones multiplicativas de módulo para generar dos partes numéricas de 4 dígitos de 1000 a 9999.

---

## 3. Diagnóstico Inteligente de Salud (Health Check)

Al iniciar, el agente ejecuta en segundo plano un script de PowerShell optimizado que extrae en menos de 2 segundos la ficha técnica del equipo y la devuelve en formato JSON al servidor Express de SV1:

* **Hardware:** Marca, modelo, procesador (CPU), porcentaje de uso, RAM total, ranuras ocupadas, capacidades, velocidades y números de serie de los módulos.
* **Discos:** Estado SMART (salud del disco), capacidad total, espacio usado y libre, marca, modelo y serie de las unidades.
* **Batería:** Si el sistema detecta que es una laptop (`BatteryStatus` de WMI), extrae la vida útil restante, porcentaje de carga y salud de la batería.
* **Eventos:** Extrae los últimos 20 errores críticos registrados en el Visor de Eventos de Windows (`Event Viewer`).
* **Red:** Tipo de adaptador (Ethernet o Wi-Fi), modelo, capacidad y latencia contra servidores DNS.

---

## 4. Loop de Sondeo y Auto-Reconexión Resiliente

El agente inicia un hilo de fondo (`RunSupportLoop`) de forma indefinida:

1. **Autenticación en el Servidor:** Realiza una petición POST a `/soporte/register` enviando su ID, hostname y JSON de salud. Añade la cabecera secreta `X-Sercom-Agent-Token` para autenticar su origen legítimo.
2. **Sondeo de Comandos (Polling):** Cada 1 segundo realiza una petición GET a `/soporte/poll?id=XXXX-XXXX` (también con el token de agente).
3. **Ejecución y Respuesta:** Si hay un comando en cola, detiene el estado visual a "Ejecutando acción remota...", corre el comando en PowerShell de forma silenciosa, y envía la salida de consola en una petición POST a `/soporte/response` antes de volver a ponerse en verde "Conectado. Esperando instrucciones...".
4. **Resiliencia ante Caídas:** Si la llamada de red falla (por ejemplo, el bot de WhatsApp en SV1 se apaga o reinicia), el bucle no se destruye. Pone el estado en "Reintentando conexión en 5s..." y reintenta de forma infinita reconectarse cada 5 segundos hasta recuperar el enlace de forma transparente.
