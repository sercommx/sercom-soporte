# Planificación y Proyección Futura - SercomDesk

Este documento establece la ruta de desarrollo (Roadmap) para la evolución del agente de soporte de Sercom Mx hacia una herramienta independiente de control remoto ("SercomDesk"), eliminando la necesidad de AnyDesk o RustDesk.

---

## Fase 1: Diagnóstico y Consola Remota (Completado)
* **Objetivos:** Recopilar estado de salud del sistema por WhatsApp e inyectar comandos de PowerShell de forma desatendida.
* **Hitos:**
  * Creación del agente gráfico WinForms con branding e icono.
  * Reporte de hardware, SMART de discos, adaptador de red y errores críticos por WhatsApp.
  * Aseguramiento del servidor con Tokens de Agente y clave de API.
  * Resiliencia a caídas de red del bot con auto-reconexión infinita.

---

## Fase 2: Sincronización de Portapapeles (Corto Plazo)
* **Objetivos:** Permitir copiar y pegar textos o archivos entre la máquina del técnico y el cliente de forma nativa.
* **Implementación:**
  * Inyectar hooks de escucha al portapapeles de Windows Forms en el agente C# usando `Clipboard.GetText()` y `Clipboard.SetText()`.
  * Endpoint HTTP `/soporte/clipboard` para sincronizar cambios en texto plano.

---

## Fase 3: Visualización de Pantalla Web Canvas (Mediano Plazo)
* **Objetivos:** Ver en tiempo real la pantalla del cliente desde un navegador web sin instalar nada del lado del técnico.
* **Implementación:**
  * **Agente C#:** Implementar captura de pantalla en bucle usando `Graphics.CopyFromScreen`. Dividir en cuadrícula (Dirty Rectangles) y enviar solo los bloques que cambian para optimizar red.
  * **Servidor (Node.js):** Crear un canal WebSocket que reciba los bytes JPEG/WebP de la pantalla y los retransmita.
  * **Panel Técnico Web:** Un lienzo HTML5 `<canvas>` que dibuje los cuadros recibidos del WebSocket y renderice la pantalla del cliente a 15-30 FPS.

---

## Fase 4: Control de Mouse/Teclado y Bypass UAC (Largo Plazo)
* **Objetivos:** Manejar el puntero, hacer clics, escribir textos e interactuar con ventanas con privilegios elevados.
* **Implementación:**
  * **Inyección de Inputs:** El lienzo Canvas en el navegador del técnico captura las coordenadas del mouse y eventos del teclado locales, los envía por WebSockets y el agente C# los inyecta en Windows usando `SendInput` de la API de Windows (`user32.dll`).
  * **Elevación de Privilegios UAC a Demanda:** Añadir un botón en el Panel Web para que el técnico solicite permisos de administrador. El agente C# ejecuta una copia de sí mismo usando `Verb = "runas"` para lanzar el prompt visual en el cliente. Si se aprueba, la sesión se reinicia con privilegios elevados permitiendo interactuar con administradores de tareas e instaladores sin congelamientos.
