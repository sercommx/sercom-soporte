/**
 * relay.js — Servidor WebSocket Relay para SerCom Soporte
 *
 * Conecta dos tipos de clientes:
 *   - type=agent&id=XXXX  →  Agente C# instalado en el equipo del cliente
 *   - type=panel&id=XXXX  →  Panel HTML5 del técnico de soporte
 *
 * Puerto por defecto: 6002
 *
 * Seguridad:
 *   - Agentes: cabecera  x-sercom-agent-token: SercomAgentToken2026SecureHashKey
 *   - Paneles:  query param  panel_key=SrC0mS0p0rt3#S3cur1tyKey#2026
 *
 * Uso ESM: import { wss, startRelayServer } from './relay.js'
 */

import { WebSocketServer, WebSocket } from 'ws';
import { URL } from 'url';

// ─────────────────────────────────────────────────────────────────────────────
//  CONSTANTES DE SEGURIDAD
// ─────────────────────────────────────────────────────────────────────────────

/** Token requerido en la cabecera HTTP de los agentes C# */
const AGENT_TOKEN   = 'SercomAgentToken2026SecureHashKey';

/** Llave requerida como query param en la URL de los paneles HTML5 */
const PANEL_KEY     = 'SrC0mS0p0rt3#S3cur1tyKey#2026';

// ─────────────────────────────────────────────────────────────────────────────
//  TIPOS DE MENSAJES PERMITIDOS POR DIRECCIÓN
// ─────────────────────────────────────────────────────────────────────────────

/** Mensajes que el agente envía hacia el panel */
const AGENT_TO_PANEL_TYPES = new Set(['frame', 'clipboard', 'cursor', 'meta']);

/** Mensajes que el panel envía hacia el agente */
const PANEL_TO_AGENT_TYPES = new Set([
  'mouse_move', 'mouse_click', 'mouse_scroll',
  'key', 'set_clipboard',
  'start_stream', 'stop_stream'
]);

// ─────────────────────────────────────────────────────────────────────────────
//  ESTADO GLOBAL
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Mapa principal de sesiones de agente.
 * Clave:  id (string)
 * Valor:  { ws: WebSocket, panel: WebSocket|null }
 *
 * @type {Map<string, { ws: WebSocket, panel: WebSocket|null }>}
 */
const agentSessions  = new Map(); // sesiones activas: agente WS conectado
const pendingPanels  = new Map(); // paneles esperando que el agente conecte su WS

// ─────────────────────────────────────────────────────────────────────────────
//  UTILIDADES
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Genera un timestamp en formato [HH:MM:SS-DD-MM-YYYY].
 * @returns {string}
 */
function timestamp() {
  const now = new Date();
  const pad = (n, l = 2) => String(n).padStart(l, '0');
  const HH = pad(now.getHours());
  const MM = pad(now.getMinutes());
  const SS = pad(now.getSeconds());
  const DD = pad(now.getDate());
  const mo = pad(now.getMonth() + 1);
  const YYYY = now.getFullYear();
  return `[${HH}:${MM}:${SS}-${DD}-${mo}-${YYYY}]`;
}

/**
 * Imprime un mensaje de log con timestamp prefijado.
 * @param {...any} args
 */
function log(...args) {
  console.log(timestamp(), ...args);
}

/**
 * Envía un objeto JSON a través de un WebSocket, solo si está abierto.
 * @param {WebSocket} ws
 * @param {object} payload
 */
function sendJSON(ws, payload) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(payload));
  }
}

/**
 * Parsea una URL de request de upgrade para obtener los query params.
 * Usa un origen ficticio porque el request.url es solo path+query.
 * @param {string} rawUrl  — p.ej. "/?type=agent&id=1234"
 * @returns {URLSearchParams}
 */
function parseQuery(rawUrl) {
  try {
    return new URL(rawUrl, 'ws://localhost').searchParams;
  } catch {
    return new URLSearchParams();
  }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SERVIDOR WEBSOCKET STANDALONE (puerto 6002)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Instancia del WebSocketServer corriendo en el puerto 6002.
 * También se exporta para integración futura con un httpServer externo.
 *
 * @type {WebSocketServer}
 */
const wss = new WebSocketServer({ port: 6002 });

log('🟢 Relay WebSocket escuchando en ws://0.0.0.0:6002');

// ─────────────────────────────────────────────────────────────────────────────
//  GESTIÓN DE CONEXIONES
// ─────────────────────────────────────────────────────────────────────────────

wss.on('connection', (ws, request) => {
  const query  = parseQuery(request.url);
  const type   = query.get('type');   // 'agent' | 'panel'
  const id     = query.get('id');     // identificador de sesión

  // ── Validación mínima de parámetros ──────────────────────────────────────
  if (!type || !id) {
    log(`⚠️  Conexión rechazada: falta type o id  [url=${request.url}]`);
    ws.close(4400, 'Se requieren los parámetros type e id');
    return;
  }

  // ── Delegación por tipo de cliente ───────────────────────────────────────
  if (type === 'agent') {
    handleAgent(ws, request, id);
  } else if (type === 'panel') {
    handlePanel(ws, request, id);
  } else {
    log(`⚠️  Tipo desconocido: "${type}" para id=${id}`);
    ws.close(4400, `Tipo de cliente desconocido: ${type}`);
  }
});

wss.on('error', (err) => {
  log('❌ Error en el servidor WebSocket:', err.message);
});

// ─────────────────────────────────────────────────────────────────────────────
//  HANDLER — AGENTE C#
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Gestiona la conexión de un agente C#.
 * Valida el token de seguridad y registra la sesión en agentSessions.
 *
 * @param {WebSocket} ws
 * @param {import('http').IncomingMessage} request
 * @param {string} id
 */
function handleAgent(ws, request, id) {
  // ── Validar token de autenticación ───────────────────────────────────────
  const token = request.headers['x-sercom-agent-token'];
  if (!token || token !== AGENT_TOKEN) {
    log(`🔒 Agente rechazado (token inválido) id=${id}`);
    ws.close(4401, 'Token de agente inválido o ausente');
    return;
  }

  log(`🤖 Agente conectado  id=${id}`);

  // Si ya existía una sesión anterior con ese id, cerrar el ws viejo
  if (agentSessions.has(id)) {
    const old = agentSessions.get(id);
    log(`♻️  Reemplazando sesión de agente existente id=${id}`);
    if (old.ws && old.ws.readyState === WebSocket.OPEN) {
      old.ws.close(4409, 'Nueva conexión de agente para este id');
    }
    // NO notificar desconexión — el agente se reconecta para streaming, conservar panel
  }

  // Buscar panel pendiente (llegó antes que el agente WS)
  let existingPanel = null;
  if (pendingPanels.has(id)) {
    const pendingWs = pendingPanels.get(id);
    if (pendingWs && pendingWs.readyState === WebSocket.OPEN) {
      existingPanel = pendingWs;
      log(`🔗 Panel pendiente vinculado al agente recién conectado id=${id}`);
    }
    pendingPanels.delete(id);
  }

  // Registrar sesión — conservar el panel pendiente o previo si sigue abierto
  agentSessions.set(id, { ws, panel: existingPanel });

  // Notificar al panel que el agente ya está listo
  if (existingPanel && existingPanel.readyState === WebSocket.OPEN) {
    sendJSON(existingPanel, { type: 'connected', id });
  }

  // ── Recepción de mensajes del agente ─────────────────────────────────────
  ws.on('message', (raw) => {
    let msg;
    try {
      msg = JSON.parse(raw.toString());
    } catch (err) {
      log(`⚠️  Mensaje inválido (JSON) del agente id=${id}:`, err.message);
      return;
    }

    const session = agentSessions.get(id);
    if (!session) return;

    if (!AGENT_TO_PANEL_TYPES.has(msg.type)) {
      log(`⚠️  Tipo de mensaje no permitido del agente id=${id}: "${msg.type}"`);
      return;
    }

    // Reenviar al panel vinculado
    if (session.panel && session.panel.readyState === WebSocket.OPEN) {
      session.panel.send(raw); // reenvío raw para evitar re-serializar frames binarios
    }
  });

  // ── Desconexión del agente ────────────────────────────────────────────────
  ws.on('close', (code, reason) => {
    log(`🔌 Agente desconectado  id=${id}  code=${code}  reason=${reason.toString()}`);

    const session = agentSessions.get(id);
    if (session) {
      // Notificar al panel vinculado
      if (session.panel && session.panel.readyState === WebSocket.OPEN) {
        sendJSON(session.panel, { type: 'agent_disconnected' });
        log(`📢 Panel notificado de desconexión del agente id=${id}`);
      }
      agentSessions.delete(id);
    }
  });

  ws.on('error', (err) => {
    log(`❌ Error en agente id=${id}:`, err.message);
  });
}

// ─────────────────────────────────────────────────────────────────────────────
//  HANDLER — PANEL HTML5
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Gestiona la conexión de un panel HTML5 de técnico.
 * Valida la panel_key y vincula el panel con la sesión del agente si existe.
 *
 * @param {WebSocket} ws
 * @param {import('http').IncomingMessage} request
 * @param {string} id
 */
function handlePanel(ws, request, id) {
  // ── Validar panel_key en query string ────────────────────────────────────
  const query    = parseQuery(request.url);
  const panelKey = query.get('panel_key');
  if (!panelKey || panelKey !== PANEL_KEY) {
    log(`🔒 Panel rechazado (panel_key inválida) id=${id}`);
    ws.close(4401, 'panel_key inválida o ausente');
    return;
  }

  log(`🖥️  Panel conectado  id=${id}`);

  // ── Vincular panel con la sesión del agente ───────────────────────────────
  const session = agentSessions.get(id);

  if (session) {
    // Desvincula panel anterior si existía
    if (session.panel && session.panel.readyState === WebSocket.OPEN) {
      log(`♻️  Reemplazando panel anterior vinculado a agente id=${id}`);
      session.panel.close(4409, 'Nueva conexión de panel para este id');
    }
    session.panel = ws;
    sendJSON(ws, { type: 'connected', id });
    log(`🔗 Panel vinculado al agente id=${id}`);
  } else {
    // Agente aún no está en el relay WS — guardar panel para vincularlo en cuanto llegue
    sendJSON(ws, { type: 'agent_offline' });
    log(`📴 Agente offline para id=${id} — panel guardado como pendiente`);
    // Limpiar panel pendiente anterior si hubiera
    if (pendingPanels.has(id)) {
      const old = pendingPanels.get(id);
      if (old && old.readyState === WebSocket.OPEN) old.close(4409, 'Nuevo panel pendiente para este id');
    }
    pendingPanels.set(id, ws);
  }

  // ── Recepción de mensajes del panel ──────────────────────────────────────
  ws.on('message', (raw) => {
    let msg;
    try {
      msg = JSON.parse(raw.toString());
    } catch (err) {
      log(`⚠️  Mensaje inválido (JSON) del panel id=${id}:`, err.message);
      return;
    }

    if (!PANEL_TO_AGENT_TYPES.has(msg.type)) {
      log(`⚠️  Tipo de mensaje no permitido del panel id=${id}: "${msg.type}"`);
      return;
    }

    // Obtener la sesión actualizada (puede haber cambiado)
    const currentSession = agentSessions.get(id);
    if (!currentSession) {
      sendJSON(ws, { type: 'agent_offline' });
      return;
    }

    // Reenviar al agente vinculado
    const agentWs = currentSession.ws;
    if (agentWs && agentWs.readyState === WebSocket.OPEN) {
      agentWs.send(raw); // reenvío raw
    } else {
      sendJSON(ws, { type: 'agent_offline' });
    }
  });

  // ── Desconexión del panel ─────────────────────────────────────────────────
  ws.on('close', (code, reason) => {
    log(`🔌 Panel desconectado  id=${id}  code=${code}  reason=${reason.toString()}`);

    // Limpiar de pendingPanels si estaba ahí
    if (pendingPanels.get(id) === ws) {
      pendingPanels.delete(id);
    }

    const currentSession = agentSessions.get(id);
    if (currentSession && currentSession.panel === ws) {
      // Notificar al agente que el panel se fue
      if (currentSession.ws && currentSession.ws.readyState === WebSocket.OPEN) {
        sendJSON(currentSession.ws, { type: 'panel_disconnected' });
        log(`📢 Agente notificado de desconexión del panel id=${id}`);
      }
      // Limpiar referencia al panel (el agente sigue vivo)
      currentSession.panel = null;
    }
  });

  ws.on('error', (err) => {
    log(`❌ Error en panel id=${id}:`, err.message);
  });
}

// ─────────────────────────────────────────────────────────────────────────────
//  INTEGRACIÓN CON SERVIDOR HTTP EXTERNO
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Conecta el relay a un servidor HTTP existente para compartir puerto mediante
 * el mecanismo de upgrade de WebSocket.
 *
 * Útil cuando relay.js se importa en un servidor Express/HTTP ya en ejecución
 * y se desea evitar abrir un segundo puerto.
 *
 * @param {import('http').Server} httpServer  — Servidor HTTP de Node.js
 * @returns {void}
 *
 * @example
 * import http from 'http';
 * import { startRelayServer } from './relay.js';
 * const server = http.createServer(app);
 * startRelayServer(server);
 * server.listen(6002);
 */
function startRelayServer(httpServer) {
  // Crear un segundo WSS sin puerto propio, vinculado al httpServer
  const attachedWss = new WebSocketServer({ noServer: true });

  // Reutilizar el mismo handler de conexión que usa `wss`
  attachedWss.on('connection', (ws, request) => {
    // Redirigir al evento 'connection' del wss principal
    wss.emit('connection', ws, request);
  });

  httpServer.on('upgrade', (request, socket, head) => {
    attachedWss.handleUpgrade(request, socket, head, (ws) => {
      attachedWss.emit('connection', ws, request);
    });
  });

  log('🔌 Relay vinculado a httpServer existente (modo upgrade)');
}

// ─────────────────────────────────────────────────────────────────────────────
//  EXPORTS ESM
// ─────────────────────────────────────────────────────────────────────────────

export { wss, agentSessions, startRelayServer };
