import { makeWASocket, useMultiFileAuthState, DisconnectReason, downloadContentFromMessage } from '@whiskeysockets/baileys';
import qrcode from 'qrcode-terminal';
import express from 'express';
import cookieParser from 'cookie-parser';
import cors from 'cors';
import { exec } from 'child_process';
import pino from 'pino';
import path from 'path';
import fs from 'fs';
import { fileURLToPath } from 'url';
import dotenv from 'dotenv';
import sqlite3 from 'sqlite3';
import dns from 'dns';
import http from 'http';
import { startRelayServer, wss } from './relay.js';
dns.setDefaultResultOrder('ipv4first');

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Cargar variables de entorno locales de whatsapp_sovereign de manera prioritaria
const localEnvPath = path.join(__dirname, '.env');
if (fs.existsSync(localEnvPath)) {
  dotenv.config({ path: localEnvPath });
} else {
  // Fallback si no existe a ingcrea (por retrocompatibilidad temporal)
  dotenv.config({ path: 'C:\\Users\\dafre\\Documents\\GitHub\\ingcrea\\.env' });
}

// Rutas dinámicas basadas en variables de entorno o fallbacks relativos
const GITHUB_ROOT = process.env.GITHUB_ROOT || 'C:\\Users\\dafre\\Documents\\GitHub';
const ALEX_OMEGA_ROOT = process.env.ALEX_OMEGA_ROOT || path.join(__dirname, '..');

// Definición dinámica de instancia y puerto para multi-cuenta
const INSTANCE = process.env.INSTANCE || 'sercom';
const PORT = process.env.PORT || 6001;

// Rutas de almacenamiento centralizadas en Z:\alex_omega\data
const DATA_DIR = path.join(ALEX_OMEGA_ROOT, 'data');
const MEDIA_DIR = path.join(DATA_DIR, 'whatsapp_media', INSTANCE);
const DB_PATH = path.join(DATA_DIR, 'whatsapp_history.db');
const AUTH_DIR = path.join(__dirname, `auth_info_${INSTANCE}`);

// Asegurar existencia de directorios base
if (!fs.existsSync(DATA_DIR)) fs.mkdirSync(DATA_DIR, { recursive: true });
if (!fs.existsSync(MEDIA_DIR)) fs.mkdirSync(MEDIA_DIR, { recursive: true });

// === CONFIGURACIÓN BASE DE DATOS SQLITE ===
const db = new sqlite3.Database(DB_PATH, (err) => {
  if (err) console.error('[SQLITE] Error al conectar con la base de datos:', err.message);
  else console.log('[SQLITE] Conectado exitosamente en:', DB_PATH);
});

// Inicializar tablas estructuradas (Ejecución serializada)
db.serialize(() => {
  // Tabla de Mensajes
  db.run(`CREATE TABLE IF NOT EXISTS messages (
    id TEXT PRIMARY KEY,
    jid TEXT NOT NULL,
    chat_name TEXT,
    from_me INTEGER NOT NULL,
    sender TEXT,
    text TEXT,
    timestamp INTEGER NOT NULL,
    instance TEXT NOT NULL
  )`);
  db.run(`CREATE INDEX IF NOT EXISTS idx_messages_jid ON messages(jid)`);
  db.run(`CREATE INDEX IF NOT EXISTS idx_messages_timestamp ON messages(timestamp)`);

  // Tabla de Multimedia
  db.run(`CREATE TABLE IF NOT EXISTS media (
    hash TEXT PRIMARY KEY,
    message_id TEXT NOT NULL,
    jid TEXT NOT NULL,
    file_path TEXT NOT NULL,
    mime_type TEXT,
    file_size INTEGER,
    timestamp INTEGER NOT NULL,
    FOREIGN KEY(message_id) REFERENCES messages(id)
  )`);
  db.run(`CREATE INDEX IF NOT EXISTS idx_media_jid ON media(jid)`);
});

// Inicializar Express
const app = express();
app.set('trust proxy', 1); // Confiar en Cloudflare (X-Forwarded-Proto)
app.use(cors());
app.use(express.json());
app.use(cookieParser('SercomSoporteSecretCookieKey2026'));

// Logger silencioso para Baileys
const logger = pino({ level: 'silent' });

let sock = null;
let connectionStatus = 'DISCONNECTED'; // DISCONNECTED, CONNECTING, WAITING_QR, SYNCHRONIZING, CONNECTED

// Estructura de caché en memoria de chats/contactos
let chatsIndex = {};

// Helper para limpiar nombres para el sistema de archivos
function sanitizeFolderName(name) {
  return name.replace(/[\/\\?%*:|"<>\s]/g, '_').trim();
}

// Helper para formatear JID
function formatJid(number) {
  if (number.endsWith('@s.whatsapp.net') || number.endsWith('@g.us')) {
    return number;
  }
  const cleanNumber = number.replace(/\D/g, '');
  return `${cleanNumber}@s.whatsapp.net`;
}

// Extractor robusto de texto de mensajes
function extractMessageText(msg) {
  if (!msg) return '[Mensaje vacío]';
  if (typeof msg.text === 'string') return msg.text;
  if (typeof msg.message === 'string') return msg.message;

  let message = msg.message;
  if (!message) return '[Mensaje sin contenido]';

  if (message.ephemeralMessage) message = message.ephemeralMessage.message;
  if (message.viewOnceMessage) message = message.viewOnceMessage.message;
  if (message.viewOnceMessageV2) message = message.viewOnceMessageV2.message;
  if (message.documentWithCaptionMessage) message = message.documentWithCaptionMessage.message;

  if (!message) return '[Mensaje multimedia / de sistema]';

  const text = message.conversation || 
               message.extendedTextMessage?.text || 
               message.imageMessage?.caption ||
               message.videoMessage?.caption ||
               message.documentMessage?.caption ||
               message.templateButtonReplyMessage?.selectedId ||
               message.buttonsResponseMessage?.selectedButtonId ||
               message.listResponseMessage?.singleSelectReply?.selectedRowId ||
               '';

  return text || '[Mensaje multimedia / de sistema]';
}

// Función para descargar y guardar multimedia organizada por conversación
async function handleIncomingMedia(msg, jid, chatName) {
  try {
    let message = msg.message;
    if (!message) return;
    
    if (message.ephemeralMessage) message = message.ephemeralMessage.message;
    if (message.viewOnceMessage) message = message.viewOnceMessage.message;
    
    const mediaType = message.imageMessage ? 'image' :
                      message.videoMessage ? 'video' :
                      message.audioMessage ? 'audio' :
                      message.documentMessage ? 'document' : null;
                      
    if (!mediaType) return;
    
    const mediaMessage = message.imageMessage || message.videoMessage || message.audioMessage || message.documentMessage;
    
    // Obtener marcas de tiempo y calcular antigüedad
    const timestamp = typeof msg.messageTimestamp === 'object' ? msg.messageTimestamp.low : (msg.messageTimestamp || msg.timestamp || Math.floor(Date.now() / 1000));
    const now = Math.floor(Date.now() / 1000);
    const ageInHours = (now - timestamp) / 3600;
    
    // Sanitizar nombres de conversación para crear carpetas legibles
    const cleanChatName = sanitizeFolderName(chatName || jid.split('@')[0]);
    const cleanJid = jid.replace(/[^a-zA-Z0-9@.]/g, '');
    const folderName = `${cleanChatName}_${cleanJid}`;
    const chatMediaDir = path.join(MEDIA_DIR, folderName, `${mediaType}s`);
    
    // Determinar nombre de archivo basándose en las propiedades conocidas del mensaje
    const mimeType = mediaMessage.mimetype || '';
    const ext = mimeType.split('/')[1]?.split(';')[0] || (mediaType === 'document' ? 'pdf' : 'bin');
    
    // Usar la clave de mediaKey como un hash de respaldo rápido si no queremos descargar el stream
    const fileHash = mediaMessage.mediaKey ? crypto.createHash('sha256').update(mediaMessage.mediaKey).digest('hex') : 'unknown';
    const fileName = mediaMessage.fileName || `${fileHash}.${ext}`;
    const finalFilePath = path.join(chatMediaDir, fileName);

    // 1. Si el archivo ya existe localmente, omitir la descarga por completo
    if (fs.existsSync(finalFilePath)) {
      return;
    }

    // 2. Si el mensaje tiene más de 12 horas de antigüedad, no intentar descargarlo
    // Los enlaces de multimedia en WhatsApp expiran a las pocas horas. Intentar descargarlos produce fallos (TypeError/404) garantizados de la API.
    if (ageInHours > 12) {
      return;
    }
    
    // Obtener stream desde Baileys para mensajes nuevos únicamente
    let stream;
    try {
      stream = await downloadContentFromMessage(mediaMessage, mediaType);
    } catch (streamError) {
      // Error de descarga de la API: no registrar ni reintentar
      return;
    }
    
    let buffer = Buffer.from([]);
    for await (const chunk of stream) {
      buffer = Buffer.concat([buffer, chunk]);
    }
    
    // Generar hash final real del buffer descargado
    const hash = crypto.createHash('sha256').update(buffer).digest('hex');
    const finalRealFileName = mediaMessage.fileName || `${hash}.${ext}`;
    const finalRealFilePath = path.join(chatMediaDir, finalRealFileName);

    if (!fs.existsSync(chatMediaDir)) {
      fs.mkdirSync(chatMediaDir, { recursive: true });
    }
    
    fs.writeFileSync(finalRealFilePath, buffer);
    
    // Insertar referencia en SQLite
    const messageId = msg.key?.id || msg.id;
    
    db.run(
      `INSERT OR IGNORE INTO media (hash, message_id, jid, file_path, mime_type, file_size, timestamp) VALUES (?, ?, ?, ?, ?, ?, ?)`,
      [hash, messageId, jid, finalRealFilePath, mimeType, buffer.length, timestamp],
      (err) => {
        if (err) console.error('[SQLITE] Error al insertar referencia multimedia:', err.message);
        else console.log(`[MULTIMEDIA GUARDADA] Referencia: ${finalRealFilePath}`);
      }
    );
  } catch (error) {
    // Captura de errores inesperados de Javascript únicamente
  }
}

// Función para buscar contexto del proyecto e inyectarlo a Ollama
function findProjectContext(prompt) {
  const promptLower = prompt.toLowerCase();
  
  const projectAliases = {
    'inseti': 'inseti',
    'megazone': 'inseti',
    'megazoneshop': 'inseti',
    'cardepot': 'cardepot',
    'car depot': 'cardepot',
    'sercom mx': 'sercommx',
    'sercommx': 'sercommx',
    'sercom': 'sercommx',
    'ingcrea': 'ingcrea',
    'ingeniería creativa': 'ingcrea',
    'ingenieria creativa': 'ingcrea',
    'greenchat': 'greenchat',
    'junkit': 'junkit',
    'abogados': 'abogados',
    'propiedades': 'propiedades',
    'tiendaic': 'tiendaic',
    'sequoiamove': 'sequoiamove',
    'inea': 'inea',
    'ochoatelier': 'ochoatelier',
    'totemprods': 'totemprods',
    'islantilla': 'islantilla/visit-islantilla-FINAL',
    'visit-islantilla': 'islantilla/visit-islantilla-FINAL',
    'visit islantilla': 'islantilla/visit-islantilla-FINAL'
  };
  
  let detectedProject = null;
  for (const alias in projectAliases) {
    if (promptLower.includes(alias)) {
      detectedProject = projectAliases[alias];
      break;
    }
  }
  
  if (!detectedProject) return null;
  
  const projectPath = path.join(GITHUB_ROOT, detectedProject);
  let context = `\n[CONTEXTO DE PROYECTO LOCAL DETECTADO: ${detectedProject.toUpperCase()}]\n`;
  let found = false;
  
  const filesToRead = ['.synapse', 'GEMINI.md', 'MAPEO.md', 'PROJECT_MAP.md', 'AUDITORIA_ALCANCE_TECNICO.md', 'CLAUDE.md', 'README.md'];
  for (const f of filesToRead) {
    const filePath = path.join(projectPath, f);
    if (fs.existsSync(filePath)) {
      try {
        const content = fs.readFileSync(filePath, 'utf-8').slice(0, 4000);
        context += `--- Archivo: ${f} ---\n${content}\n\n`;
        found = true;
      } catch (e) {
        // Ignorar
      }
    }
  }
  
  return found ? context : null;
}

// Función asíncrona para buscar identidades de personas
function findPersonContext(prompt) {
  return new Promise((resolve) => {
    const lower = prompt.toLowerCase();
    const isPersonQuery = lower.includes('persona') || 
                          lower.includes('identidad') || 
                          lower.includes('buscar a') || 
                          lower.includes('busca a') ||
                          lower.includes('quién es') || 
                          lower.includes('quien es') ||
                          lower.includes('curp') ||
                          lower.includes('rfc') ||
                          lower.includes('ericka') ||
                          lower.includes('karina') ||
                          lower.includes('rivera') ||
                          lower.includes('morales');
                           
    if (!isPersonQuery) return resolve(null);
    
    let searchTerm = prompt
      .replace(/dame información sobre la identidad de la persona/gi, '')
      .replace(/dame información sobre la identidad de/gi, '')
      .replace(/dame información sobre/gi, '')
      .replace(/busca a la persona/gi, '')
      .replace(/buscar a la persona/gi, '')
      .replace(/busca a/gi, '')
      .replace(/buscar a/gi, '')
      .replace(/quien es/gi, '')
      .replace(/quién es/gi, '')
      .replace(/usando tu base de datos de personas/gi, '')
      .replace(/base de datos de personas/gi, '')
      .replace(/['"]/g, '')
      .trim();
       
    if (searchTerm.length < 3) return resolve(null);
    
    const pythonPath = path.join(ALEX_OMEGA_ROOT, 'core', 'venv', 'bin', 'python3');
    const scriptPath = path.join(__dirname, 'search_person_db.py');
    const escapedTerm = searchTerm.replace(/"/g, '\\"');
    const command = `"${pythonPath}" "${scriptPath}" "${escapedTerm}"`;
    
    exec(command, { timeout: 12000 }, (error, stdout, stderr) => {
      if (error) {
        console.error('[PERSON SEARCH] Error al buscar persona:', error.message);
        return resolve(null);
      }
      try {
        const data = JSON.parse(stdout.trim());
        if (data.results && data.results.length > 0) {
          let context = `\n[CONTEXTO DE IDENTIDAD DETECTADO DESDE LA BASE DE DATOS DUCKDB SOBERANA DE ALEX]\n`;
          data.results.forEach((p, idx) => {
            context += `Persona #${idx + 1}:\n`;
            context += `  - Nombre Completo: ${p.nombre_completo}\n`;
            context += `  - CURP: ${p.curp || 'No registrado'}\n`;
            context += `  - RFC: ${p.rfc || 'No registrado'}\n`;
            context += `  - Dirección: ${p.direccion || 'No registrada'}\n`;
            if (p.telefono) context += `  - Teléfono: ${p.telefono}\n`;
            context += `\n`;
          });
          return resolve(context);
        }
      } catch (e) {
        // Falló parsing
      }
      resolve(null);
    });
  });
}

// Función asíncrona para buscar y cotizar productos en Syscom
function findSyscomContext(prompt) {
  return new Promise((resolve) => {
    const lower = prompt.toLowerCase();
    const isSyscomQuery = lower.includes('syscom') || 
                          lower.includes('cotiza') || 
                          lower.includes('cotizar') || 
                          lower.includes('precio de') || 
                          lower.includes('busca en syscom') ||
                          lower.includes('buscar en syscom');
                           
    if (!isSyscomQuery) return resolve(null);
    
    let searchTerm = prompt
      .replace(/cotiza la persona/gi, '')
      .replace(/cotizar la persona/gi, '')
      .replace(/busca en syscom/gi, '')
      .replace(/buscar en syscom/gi, '')
      .replace(/precio de/gi, '')
      .replace(/cotiza/gi, '')
      .replace(/cotizar/gi, '')
      .replace(/syscom/gi, '')
      .replace(/['"]/g, '')
      .trim();
       
    if (searchTerm.length < 3) return resolve(null);
    
    const pythonPath = path.join(ALEX_OMEGA_ROOT, 'core', 'venv', 'bin', 'python3');
    const scriptPath = path.join(__dirname, 'search_syscom.py');
    const escapedTerm = searchTerm.replace(/"/g, '\"');
    const command = `"${pythonPath}" "${scriptPath}" "${escapedTerm}"`;
    
    exec(command, { timeout: 15000 }, (error, stdout, stderr) => {
      if (error) {
        console.error('[SYSCOM SEARCH] Error al buscar producto:', error.message);
        return resolve(null);
      }
      try {
        const data = JSON.parse(stdout.trim());
        if (data.results && data.results.length > 0) {
          let context = `\n[CONTEXTO DE PRODUCTOS Y COTIZACIÓN DE SYSCOM EN TIEMPO REAL]\n`;
          context += `Tipo de cambio del día: $${data.exchange_rate} MXN/USD\n\n`;
          data.results.forEach((p, idx) => {
            context += `Producto #${idx + 1}:\n`;
            context += `  - Modelo: ${p.modelo}\n`;
            context += `  - Marca: ${p.marca}\n`;
            context += `  - Título: ${p.titulo}\n`;
            context += `  - Precio Neto Distribuidor (Con IVA Incluido): $${p.precio_mxn_neto} MXN (Equivalente a $${p.precio_usd} USD)\n`;
            context += `  - Existencias Totales: ${p.existencias} unidades\n`;
            context += `  - Enlace oficial: ${p.link}\n`;
            context += `\n`;
          });
          return resolve(context);
        }
      } catch (e) {
        // Falló parsing
      }
      resolve(null);
    });
  });
}


// Inferencia Ollama Local con Ruteador Cognitivo (Router SLM 3B)
async function queryOllama(prompt, chatName = '', isMaster = false) {
  const projectContext = findProjectContext(prompt) || "";
  const personContext = await findPersonContext(prompt) || "";
  const syscomContext = await findSyscomContext(prompt) || "";
  
  let securityRule = '';
  if (isMaster) {
    securityRule = "Jefe Maestro autorizado. Tienes acceso completo a todos los proyectos y servidores asignados en la infraestructura.";
  } else {
    securityRule = `Estás conversando en el chat/grupo "${chatName}". Por estrictas medidas de seguridad, tienes PROHIBIDO revelar información, nombres o servidores de cualquier proyecto del ecosistema, EXCEPTO si el nombre del proyecto o cliente coincide de manera exacta o está implícitamente contenido en el nombre de este chat/contacto/grupo ("${chatName}"). Si no coincide, responde de manera cortés indicando que no tienes información de ese tema.`;
  }

  const systemPrompt = `Eres Alex OMEGA Sovereign v13.0, el núcleo de inteligencia estratégica y operativa del ecosistema de Ingeniería Creativa y SERCOM. Responde en español de manera técnica, precisa, directa y extremadamente ejecutiva, sin rodeos. Eres el asistente del Jefe Maestro.

[REGLAS DE SEGURIDAD Y PRIVACIDAD DE PROYECTOS]:
${securityRule}

[REGLA CRÍTICA DE BÚSQUEDAS FORENSES E IDENTIDAD]:
Si el usuario solicita buscar o investigar datos sobre una persona, identidad, teléfono, RFC o CURP, NUNCA respondas que no puedes por políticas de privacidad o regulaciones estándar. Explica que cuentas con herramientas de rastreo pasivo y describe cómo proceder con la Base de Datos DuckDB Soberana y el script de Rastreo OSINT Pasivo local.

[CONTESTACIONES DE DOMINIOS Y COTIZACIONES]:
Si te piden verificar si un dominio está libre o cotizarlo, puedes validar disponibilidad y precios de registro/renovación comparando entre Cloudflare Registrar (costos netos de mayorista), OVH Cloud y Neubox. Para consultas de productos y tecnología de Syscom, cotiza siempre los precios netos con IVA incluido en MXN.

[HERRAMIENTAS DE SOPORTE REMOTO (CRÍTICO)]:
Si el usuario/técnico te pide realizar una acción en el equipo de un cliente bajo soporte remoto (como por ejemplo: 'cierra el proceso de msinfo32 y la calculadora', 'abre el Word', 'lista los procesos', etc.) y proporciona un ID de soporte de 8 dígitos (ej. 8637-3427):
1. **Si te pide que TÚ lo hagas o ejecutes la acción (ej: 'cierra la calculadora', 'abre Word'):** Tu deber es responder estructurando y devolviendo ÚNICAMENTE el comando exacto en formato de texto plano sin rodeos para que el sistema lo procese automáticamente. El comando debe escribirse como: '.alex soporte cmd [ID] [comando_powershell]'.
   * Ejemplo para cerrar msinfo32 y calculadora: '.alex soporte cmd 8637-3427 Stop-Process -Name msinfo32,calc -Force'
   * Ejemplo para abrir Word: '.alex soporte cmd 8637-3427 Start-Process winword'
2. **Si el usuario te pregunta explícitamente CÓMO hacerlo (ej: 'dime cómo puedo cerrar la calculadora en el cliente' o 'cómo se abre Word'):** Entonces debes responder con explicaciones textuales paso a paso instruyendo al técnico a usar el comando '.alex soporte cmd [ID] [comando_powershell]'.

[CONTEXTO LOCAL]:
${projectContext}
${personContext}
${syscomContext}`;

  // Puerto/Host configurable por variable de entorno
  let ollamaHost = process.env.OLLAMA_HOST || 'http://127.0.0.1:11434';
  if (process.env.OLLAMA_PORT && !process.env.OLLAMA_HOST) {
    ollamaHost = `http://127.0.0.1:${process.env.OLLAMA_PORT}`;
  }

  // Mapa de decisiones de modelo
  const modelMap = {
    'MINI': 'AlexMini',
    'CODERMINI': 'AlexCoderMini',
    'CODER': 'AlexCoder',
    'THINKER': 'AlexOmega'
  };

  const classifyQuery = async () => {
    const classificationPrompt = `Eres el clasificador de intenciones del sistema Alex OMEGA.
Tu única tarea es analizar la consulta del usuario y responder con UNA sola palabra en mayúsculas de esta lista: [MINI, CODERMINI, CODER, THINKER].

Reglas de decisión:
- Responde 'CODERMINI' si la consulta es sobre sintaxis de código, explicar fragmentos de código, solucionar un error de compilación sencillo, o escribir scripts cortos de menos de 15 líneas.
- Responde 'CODER' si el usuario pide programar un plugin entero, crear páginas web completas (Astro, Tailwind, Flutter), integrar APIs complejas, o realizar modificaciones profundas al código del proyecto.
- Responde 'THINKER' si pide análisis forense de seguridad, peritaje digital, desencriptar archivos, auditorías de logs complejas, consultoría estratégica de TI, o razonamiento lógico muy complejo.
- Responde 'MINI' si es charla general, saludos, palabras sueltas, dudas de negocio, agendar reuniones en Calendar, interactuar de manera administrativa en WhatsApp con clientes, o cualquier frase corta que no contenga código o requerimientos de programación.

Responde únicamente con la palabra clave sin puntuación, espacios ni explicaciones adicionales.

Consulta del usuario: ${prompt}
Clasificación:`;

    try {
      const response = await fetch(`${ollamaHost}/api/generate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          model: 'AlexMini',
          prompt: classificationPrompt,
          stream: false,
          options: {
            temperature: 0.0,
            num_predict: 5
          }
        })
      });

      if (response.ok) {
        const data = await response.json();
        const rawDecision = (data.response || '').trim().toUpperCase();
        console.log(`[ROUTER] Resultado raw del clasificador: '${rawDecision}'`);
        const cleanDecision = rawDecision.replace(/[^A-Z]/g, '');
        if (modelMap[cleanDecision]) {
          return cleanDecision;
        }
      }
      return 'MINI';
    } catch (e) {
      console.warn('[ROUTER] Error al clasificar consulta, usando fallback MINI:', e.message);
      return 'MINI';
    }
  };

  const performFetch = async (targetModel) => {
    console.log(`[ROUTER] Ejecutando consulta con el modelo seleccionado: ${targetModel}`);
    const response = await fetch(`${ollamaHost}/api/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model: targetModel,
        messages: [
          { role: 'system', content: systemPrompt },
          { role: 'user', content: prompt }
        ],
        stream: false,
        options: {
          temperature: 0.1
        }
      })
    });
    
    if (response.ok) {
      const data = await response.json();
      return data.message?.content || '[Modelo no retornó contenido]';
    }
    return `[Error de Inferencia: HTTP ${response.status}]`;
  };

  try {
    const decision = await classifyQuery();
    const selectedModel = modelMap[decision] || 'AlexMini';
    console.log(`[ROUTER] Intención: ${decision} ──► Modelo: ${selectedModel}`);
    return await performFetch(selectedModel);
  } catch (error) {
    console.log('[AUTOREPARACIÓN] Falló la conexión con Ollama. Intentando levantar o reiniciar el servicio...');
    try {
      await new Promise((resolve) => {
        const cmd = process.env.USER === 'root' ? 'systemctl restart ollama' : "echo 'S3rC0mMx' | sudo -S systemctl restart ollama || systemctl restart ollama";
        exec(cmd, (err, stdout, stderr) => {
          if (err) {
            console.error('[AUTOREPARACIÓN] Error al reiniciar Ollama:', err.message);
          }
          setTimeout(resolve, 5000);
        });
      });
      console.log('[AUTOREPARACIÓN] Reintentando consulta a Ollama tras el reinicio...');
      const decision = await classifyQuery();
      const selectedModel = modelMap[decision] || 'AlexMini';
      const result = await performFetch(selectedModel);
      return `*(Ollama presentaba una falla y ha sido autoreparado/reiniciado automáticamente)*\n\n` + result;
    } catch (retryError) {
      console.error('[AUTOREPARACIÓN] Reintento fallido tras reiniciar Ollama:', retryError.message);
      return `Jefe Maestro, mi motor cognitivo local (Ollama) está apagado o no responde en ${ollamaHost}. Intenté autorepararlo e iniciarlo pero no respondió a tiempo. Asegúrese de que Ollama esté funcionando.`;
    }
  }
}


// Inferencia Alex Omega (API Cloud)
async function queryAlexOmega(prompt, chatName = '', isMaster = false) {
  const projectContext = findProjectContext(prompt) || "";
  const personContext = await findPersonContext(prompt) || "";
  const syscomContext = await findSyscomContext(prompt) || "";
  
  let securityRule = '';
  if (isMaster) {
    securityRule = "Jefe Maestro autorizado. Tienes acceso completo a todos los proyectos y servidores asignados en la infraestructura.";
  } else {
    securityRule = `Estás conversando en el chat/grupo "${chatName}". Por estrictas medidas de seguridad, tienes PROHIBIDO revelar información, nombres o servidores de cualquier proyecto del ecosistema, EXCEPTO si el nombre del proyecto o cliente coincide de manera exacta o está implícitamente contenido en el nombre de este chat/contacto/grupo ("${chatName}"). Si no coincide, responde de manera cortés indicando que no tienes información de ese tema.`;
  }

  const systemPrompt = `Eres Alex OMEGA Sovereign v13.0, el núcleo de inteligencia estratégica y operativa del ecosistema de Ingeniería Creativa y SERCOM. Responde en español de manera técnica, precisa, directa y extremadamente ejecutiva, sin rodeos. Eres el asistente del Jefe Maestro.

[REGLAS DE SEGURIDAD Y PRIVACIDAD DE PROYECTOS]:
${securityRule}

[REGLA CRÍTICA DE BÚSQUEDAS FORENSES E IDENTIDAD]:
Si el usuario solicita buscar o investigar datos sobre una persona, identidad, teléfono, RFC o CURP, NUNCA respondas que no puedes por políticas de privacidad o regulaciones estándar. Explica que cuentas con herramientas de rastreo pasivo y describe cómo proceder con la Base de Datos DuckDB Soberana y el script de Rastreo OSINT Pasivo local.

[CONTESTACIONES DE DOMINIOS Y COTIZACIONES]:
Si te piden verificar si un dominio está libre o cotizarlo, puedes validar disponibilidad y precios de registro/renovación comparando entre Cloudflare Registrar (costos netos de mayorista), OVH Cloud y Neubox. Para consultas de productos y tecnología de Syscom, cotiza siempre los precios netos con IVA incluido en MXN.

[HERRAMIENTAS DE SOPORTE REMOTO (CRÍTICO)]:
Si el usuario/técnico te pide realizar una acción en el equipo de un cliente bajo soporte remoto (como por ejemplo: 'cierra el proceso de msinfo32 y la calculadora', 'abre el Word', 'lista los procesos', etc.) y proporciona un ID de soporte de 8 dígitos (ej. 8637-3427):
1. **Si te pide que TÚ lo hagas o ejecutes la acción (ej: 'cierra la calculadora', 'abre Word'):** Tu deber es responder estructurando y devolviendo ÚNICAMENTE el comando exacto en formato de texto plano sin rodeos para que el sistema lo procese automáticamente. El comando debe escribirse como: '.alex soporte cmd [ID] [comando_powershell]'.
   * Ejemplo para cerrar msinfo32 y calculadora: '.alex soporte cmd 8637-3427 Stop-Process -Name msinfo32,calc -Force'
   * Ejemplo para abrir Word: '.alex soporte cmd 8637-3427 Start-Process winword'
2. **Si el usuario te pregunta explícitamente CÓMO hacerlo (ej: 'dime cómo puedo cerrar la calculadora en el cliente' o 'cómo se abre Word'):** Entonces debes responder con explicaciones textuales paso a paso instruyendo al técnico a usar el comando '.alex soporte cmd [ID] [comando_powershell]'.

[CONTEXTO LOCAL]:
${projectContext}
${personContext}
${syscomContext}`;

  const apiKey = process.env.DEEPSEEK_API_KEY || process.env.OPENAI_API_KEY;
  if (!apiKey) return "Jefe Maestro, no he encontrado una API Key de DeepSeek o OpenAI en el archivo .env.";

  const isDeepSeek = apiKey.startsWith("sk-6") || apiKey.includes("deepseek");
  const url = isDeepSeek ? 'https://api.deepseek.com/chat/completions' : 'https://api.openai.com/v1/chat/completions';
  const model = isDeepSeek ? (process.env.DEEPSEEK_MODEL || 'deepseek-chat') : 'gpt-4o-mini';

  try {
    const response = await fetch(url, {
      method: 'POST',
      headers: { 
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${apiKey}`
      },
      body: JSON.stringify({
        model: model,
        messages: [
          { role: 'system', content: systemPrompt },
          { role: 'user', content: prompt }
        ],
        temperature: 0.1
      })
    });

    if (response.ok) {
      const data = await response.json();
      return data.choices?.[0]?.message?.content || '[No se recibió respuesta del modelo]';
    }
    return `[Error API Alex Omega: HTTP ${response.status}]`;
  } catch (error) {
    return `Jefe Maestro, falló la conexión con la API de Alex Omega: ${error.message}`;
  }
}

// Cliente cognitivo Antigravity CLI (se comunica con el servicio agy local mediante túnel inverso)
function queryAgyCli(prompt) {
  return new Promise(async (resolve) => {
    // Aumentamos el timeout de contingencia a 115 segundos, ya que agy local puede tardar analizando código real
    const timer = setTimeout(async () => {
      console.warn('[AGY CLI] Timeout del servicio de agy en localhost:5001, cayendo en contingencia...');
      const fallbackResponse = await queryOllama(prompt);
      resolve(`[Contingencia Local] ${fallbackResponse}`);
    }, 115000);

    try {
      const res = await fetch('http://127.0.0.1:5001/query', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ prompt })
      });

      clearTimeout(timer);

      if (res.ok) {
        const data = await res.json();
        resolve(data.response || '[Servicio agy retornó respuesta vacía]');
      } else {
        const errText = await res.text();
        console.warn(`[AGY CLI] Error HTTP ${res.status} desde agy-service:`, errText);
        const fallbackResponse = await queryOllama(prompt);
        resolve(fallbackResponse);
      }
    } catch (err) {
      clearTimeout(timer);
      console.warn('[AGY CLI] Falló la conexión con agy-service en localhost:5001:', err.message);
      const fallbackResponse = await queryOllama(prompt);
      resolve(fallbackResponse);
    }
  });
}

// Realiza peticiones HTTP a la API de Mega-Debrid enrutadas por el túnel Socks5 de la IP residencial de SV4
function curlRequest(url) {
  return new Promise((resolve, reject) => {
    const cmd = `curl -s --socks5-hostname localhost:1080 -H "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" "${url}"`;
    exec(cmd, (err, stdout, stderr) => {
      if (err) return reject(err);
      try {
        const json = JSON.parse(stdout);
        resolve(json);
      } catch (e) {
        reject(new Error(stdout || stderr || "Respuesta vacía"));
      }
    });
  });
}

// Descarga archivos de Mega-Debrid y los sube a Google Drive usando rclone
async function handleDownloadAndUploadToDrive(premiumLink, jid) {
  try {
    let filename = premiumLink.split('/').pop().split('?')[0] || "descarga_" + Date.now();
    filename = decodeURIComponent(filename);
    
    // Limpieza de caracteres no válidos para el nombre de archivo en linux/gdrive
    filename = filename.replace(/[\\/*?:"<>|]/g, "_");
    const downloadPath = path.join('/tmp', filename);
    
    console.log(`[DESCARGA] Descargando ${premiumLink} en ${downloadPath}...`);
    
    // Descarga robusta usando curl con reintentos y redirección
    exec(`curl -L -o "${downloadPath}" "${premiumLink}"`, async (downloadErr) => {
      if (downloadErr) {
        console.error('[DESCARGA] Error en la descarga del archivo:', downloadErr);
        await sock.sendMessage(jid, { text: `❌ *Error al descargar el archivo en el servidor:* ${downloadErr.message}` });
        return;
      }
      
      await sock.sendMessage(jid, { text: `📤 *Descarga finalizada en el servidor.* Subiendo "${filename}" a tu Google Drive mediante rclone...` });
      
      // Comando para mover el archivo usando rclone al remoto "gdrive" en la carpeta "MegaDebrid" (elimina origen tras subida exitosa)
      exec(`rclone move "${downloadPath}" gdrive:MegaDebrid/`, async (uploadErr, stdout, stderr) => {
        // Limpieza de seguridad por si rclone falló o dejó residuos
        fs.unlink(downloadPath, () => {});
        
        if (uploadErr) {
          console.error('[RCLONE] Error subiendo a GDrive:', uploadErr, stderr);
          await sock.sendMessage(jid, { text: `⚠️ *Archivo descargado, pero falló la subida a Google Drive:* \n\n${stderr || uploadErr.message}\n\n*Nota:* Asegúrate de que la API de Google Drive esté habilitada en tu consola de desarrollador para este proyecto.` });
        } else {
          await sock.sendMessage(jid, { text: `🎉 *¡Descarga y subida completadas!* El archivo *"${filename}"* ha sido almacenado directamente en tu Google Drive (carpeta: MegaDebrid) y eliminado del servidor.` });
        }
      });
    });
  } catch (err) {
    console.error('[DOWNLOAD_DRIVE] Error general:', err);
    await sock.sendMessage(jid, { text: `❌ *Error inesperado:* ${err.message}` });
  }
}

// === COLA DE TAREAS EN MEMORIA PARA IA (DESACOPLADA) ===
const messageQueue = [];
let processingQueue = false;

async function processQueue() {
  if (processingQueue || messageQueue.length === 0) return;
  processingQueue = true;

  const task = messageQueue.shift();
  try {
    let responseText = '';
    if (task.type === 'mini') {
      responseText = await queryOllama(task.prompt, task.chatName, task.isMaster);
    } else if (task.type === 'omega') {
      responseText = await queryAlexOmega(task.prompt, task.chatName, task.isMaster);
    } else if (task.type === 'agy') {
      responseText = await queryAgyCli(task.prompt);
    }

    if (responseText) {
      await sock.sendMessage(task.jid, { text: responseText });
    }
  } catch (err) {
    console.error('[COLA] Error procesando tarea cognitiva:', err);
  }

  processingQueue = false;
  setImmediate(processQueue); // Continuar con la siguiente tarea
}

function enqueueCognitiveTask(type, prompt, jid, chatName = '', isMaster = false) {
  messageQueue.push({ type, prompt, jid, chatName, isMaster });
  setImmediate(processQueue);
}

// === SISTEMA DE ALMACENAMIENTO SQLITE SOBERANO ===

function saveChatHistoryToDatabase(jid, name, newMessages) {
  let addedCount = 0;
  
  db.serialize(() => {
    const stmt = db.prepare(`INSERT OR IGNORE INTO messages (id, jid, chat_name, from_me, sender, text, timestamp, instance) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`);
    
    newMessages.forEach(msg => {
      const msgId = msg.key?.id || msg.id;
      if (!msgId) return;

      const text = extractMessageText(msg);
      const timestamp = typeof msg.messageTimestamp === 'object' ? msg.messageTimestamp.low : (msg.messageTimestamp || msg.timestamp || Math.floor(Date.now() / 1000));
      const fromMe = msg.key ? (msg.key.fromMe ? 1 : 0) : (msg.fromMe ? 1 : 0);
      const sender = msg.pushName || msg.sender || (fromMe ? 'Alex OMEGA (Bot)' : 'Cliente');

      stmt.run(msgId, jid, name || jid, fromMe, sender, text, timestamp, INSTANCE);
      addedCount++;
      
      // Manejar descargas multimedia de forma asíncrona fuera del loop
      handleIncomingMedia(msg, jid, name);
    });
    
    stmt.finalize();
  });

  if (newMessages.length > 0) {
    const lastMsg = newMessages[newMessages.length - 1];
    updateChatsIndex(jid, name, { text: extractMessageText(lastMsg), timestamp: lastMsg.timestamp || Math.floor(Date.now() / 1000) });
  }

  return addedCount;
}

// Mantener el índice rápido en memoria y persistir una copia ligera
const INDEX_FILE = path.join(DATA_DIR, `chats_index_${INSTANCE}.json`);
if (fs.existsSync(INDEX_FILE)) {
  try {
    chatsIndex = JSON.parse(fs.readFileSync(INDEX_FILE, 'utf-8'));
  } catch (e) {
    chatsIndex = {};
  }
}

function updateChatsIndex(jid, name, lastMsg, archive = null) {
  const prevArchive = chatsIndex[jid]?.archive === true;
  chatsIndex[jid] = {
    jid,
    name: name || jid,
    lastMessage: lastMsg?.text || '[Sin mensajes]',
    timestamp: lastMsg?.timestamp || 0,
    archive: (archive === true || archive === false) ? archive : prevArchive
  };

  fs.writeFile(INDEX_FILE, JSON.stringify(chatsIndex, null, 2), 'utf-8', (err) => {
    if (err) console.error('Error al guardar el índice de chats:', err);
  });
}

function drawProgressBar(current, total, label = '') {
  const percentage = Math.round((current / total) * 100) || 0;
  const barLength = 20;
  const filledLength = Math.round((current / total) * barLength) || 0;
  const bar = '█'.repeat(filledLength) + '░'.repeat(barLength - filledLength);
  
  if (process.stdout.isTTY) {
    process.stdout.clearLine(0);
    process.stdout.cursorTo(0);
    process.stdout.write(`[PROGRESO] [${bar}] ${percentage}% | ${current}/${total} ${label}`);
  } else {
    if (current % Math.max(1, Math.round(total / 10)) === 0 || current === total) {
      console.log(`[PROGRESO] [${bar}] ${percentage}% | ${current}/${total} ${label}`);
    }
  }
}

// === CONEXIÓN WHATSAPP ===
async function connectToWhatsApp() {
  const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR);

  sock = makeWASocket({
    auth: state,
    printQRInTerminal: false,
    logger,
    browser: ['Windows', 'Chrome', '133.0.0.0'],
    connectTimeoutMs: 60000,
    defaultQueryTimeoutMs: 0,
    syncFullHistory: true
  });

  connectionStatus = 'CONNECTING';

  sock.ev.on('connection.update', (update) => {
    const { connection, lastDisconnect, qr } = update;
    if (qr) {
      connectionStatus = 'WAITING_QR';
      console.clear();
      console.log('=== ESCANEA ESTE CÓDIGO QR CON TU WHATSAPP ===');
      qrcode.generate(qr, { small: true });
      console.log('============================================');
    }

    if (connection === 'close') {
      const error = lastDisconnect?.error;
      const statusCode = error?.output?.statusCode;
      const shouldReconnect = statusCode !== DisconnectReason.loggedOut;
      
      console.log(`Conexión cerrada. Status: ${statusCode}. Reintentando conexión...`);
      connectionStatus = 'DISCONNECTED';
      if (shouldReconnect) setTimeout(connectToWhatsApp, 5000);
    } else if (connection === 'open') {
      connectionStatus = 'CONNECTED';
      console.clear();
      console.log('¡WhatsApp Sovereign Bot conectado exitosamente!');
    }
  });

  sock.ev.on('creds.update', saveCreds);

  sock.ev.on('chats.set', ({ chats }) => {
    chats.forEach(chat => {
      const isArchived = chat.archive === true || chat.archived === true;
      updateChatsIndex(chat.id, chat.name || chat.id, null, isArchived);
    });
  });

  sock.ev.on('messages.upsert', async (m) => {
    if (m.type === 'notify') {
      for (const msg of m.messages) {
        const jid = msg.key.remoteJid;
        if (chatsIndex[jid]?.archive === true) continue;

        const text = extractMessageText(msg);
        const textLower = text.toLowerCase();
        
        // Soporte tanto para prefijo / como .
        let isLink = textLower.startsWith('/alex link ') || textLower.startsWith('.alex link ') || textLower === '/alex link' || textLower === '.alex link';
        let isDescargar = textLower.startsWith('/alex descargar ') || textLower.startsWith('.alex descargar ') || textLower === '/alex descargar' || textLower === '.alex descargar';
        let isSoporte = textLower.startsWith('/alex soporte') || textLower.startsWith('.alex soporte');
        let isAgy = (textLower.startsWith('/alexg ') || textLower === '/alexg' || textLower.startsWith('.alexg ') || textLower === '.alexg' || textLower.startsWith('/alex ') || textLower === '/alex' || textLower.startsWith('.alex ') || textLower === '.alex') && !isLink && !isDescargar && !isSoporte;
        let isMini = textLower.startsWith('/alexm ') || textLower === '/alexm' || textLower.startsWith('.alexm ') || textLower === '.alexm';
        let isOmega = textLower.startsWith('/alexo ') || textLower === '/alexo' || textLower.startsWith('.alexo ') || textLower === '.alexo';
        
        const isCommand = isAgy || isMini || isOmega || isLink || isDescargar || isSoporte;
        const fromMe = msg.key.fromMe;

        const fromName = msg.pushName || (fromMe ? 'Alex OMEGA (Bot)' : 'Desconocido');
        const isGroup = jid.endsWith('@g.us');
        let chatName = chatsIndex[jid]?.name || fromName;

        // Filtrar mensajes de protocolo y estado interno (sin texto real ni multimedia que procesar)
        if (!text || text === '[Mensaje sin contenido]' || text === '[Mensaje multimedia / de sistema]' || msg.message?.protocolMessage) {
          continue;
        }

        // 1. Obtener y formatear fecha/hora actual local
        const dateObj = new Date();
        const HH = String(dateObj.getHours()).padStart(2, '0');
        const MM = String(dateObj.getMinutes()).padStart(2, '0');
        const SS = String(dateObj.getSeconds()).padStart(2, '0');
        const DD = String(dateObj.getDate()).padStart(2, '0');
        const MO = String(dateObj.getMonth() + 1).padStart(2, '0');
        const YYYY = dateObj.getFullYear();
        const formattedTime = `[${HH}:${MM}:${SS}-Fecha ${DD}-${MO}-${YYYY}]`;

        // 2. Extraer remitente del mensaje (Nombre y Número si aplica)
        let senderInfo = '';
        if (fromMe) {
          senderInfo = 'Yo (Bot)';
        } else {
          const senderJid = msg.key.participant || jid;
          let senderNumber = senderJid.split('@')[0];
          
          // Resolución dinámica de LID usando los archivos de mapeo de Baileys
          if (senderJid.endsWith('@lid')) {
            try {
              const lidFile = path.join(AUTH_DIR, `lid-mapping-${senderNumber}.json`);
              if (fs.existsSync(lidFile)) {
                let mappedJid = JSON.parse(fs.readFileSync(lidFile, 'utf8'));
                if (mappedJid) {
                  senderNumber = mappedJid.split('@')[0];
                }
              }
            } catch (resolveErr) {
              // Mapeo fallback alternativo con chatsIndex
              const knownChat = Object.values(chatsIndex).find(c => c.name === fromName && c.jid && !c.jid.endsWith('@lid'));
              if (knownChat) {
                senderNumber = knownChat.jid.split('@')[0];
              }
            }
          }

          // Limpiar formato de servidor de WhatsApp (ej. 521XXXXXXXXX o 521XXXXXXXXXX -> 52XXXXXXXXXX)
          if (senderNumber.startsWith('521') && senderNumber.length === 13) {
            senderNumber = '52' + senderNumber.slice(3);
          }
          senderInfo = `${fromName} (${senderNumber})`;
        }

        // 3. Determinar tipo de conversación e imprimir el log completo
        // Si es individual, mostrar el número del chat destino
        let cleanChatJid = jid.split('@')[0];
        if (jid.endsWith('@lid')) {
          try {
            const lidFile = path.join(AUTH_DIR, `lid-mapping-${cleanChatJid}.json`);
            if (fs.existsSync(lidFile)) {
              let mappedJid = JSON.parse(fs.readFileSync(lidFile, 'utf8'));
              if (mappedJid) {
                cleanChatJid = mappedJid.split('@')[0];
              }
            }
          } catch (resolveErr) {
            const knownChat = Object.values(chatsIndex).find(c => c.name === chatName && c.jid && !c.jid.endsWith('@lid'));
            if (knownChat) {
              cleanChatJid = knownChat.jid.split('@')[0];
            }
          }
        }
        if (cleanChatJid.startsWith('521') && cleanChatJid.length === 13) {
          cleanChatJid = '52' + cleanChatJid.slice(3);
        }
        const chatTypeLabel = isGroup ? `Grupo: ${chatName}` : `INDIVIDUAL (${cleanChatJid})`;
        console.log(`${formattedTime} <${senderInfo}> <${chatTypeLabel}> <${text}>`);



        // 4. Guardar incondicionalmente en la Base de Datos SQLite
        if (isGroup) {
          if (!chatName || chatName === jid) {
            try {
              const metadata = await sock.groupMetadata(jid);
              chatName = metadata.subject || jid;
              updateChatsIndex(jid, chatName, null);
            } catch (e) {
              chatName = 'Grupo de WhatsApp';
            }
          }
          saveChatHistoryToDatabase(jid, chatName, [msg]);
        } else {
          saveChatHistoryToDatabase(jid, chatName, [msg]);
        }

        // --- SISTEMA DE ENCOLADO ASÍNCRONO DE INVOCACIONES (IGNORA MENSAJES QUE NO SEAN COMANDOS) ---
        if (msg.key.fromMe && !isCommand) continue;

        const senderJid = msg.key.participant || jid;
        const isMaster = fromMe || senderJid.includes('5218146529034') || senderJid.includes('528146529034') || senderJid.includes('5218180234638');

        if (isMini) {
          let prompt = text.replace(/^[\/\.](alexm|alex)/i, "").trim();
          let promptLower = prompt.toLowerCase();
          if (!prompt || promptLower === 'menu' || promptLower === 'help') {
              await sock.sendMessage(jid, { text: `🤖 *¡Hola! Soy Alex, tu asistente cognitivo soberano de IA.*

Estoy aquí para ayudarte a optimizar, gestionar y auditar tus sistemas. Estas son mis principales *capacidades y funciones*:

⚡ *1. Gestión y Auditoría de Proyectos en Curso:*
• Listado detallado de proyectos activos en infraestructura propia y asignación de servidores correspondientes (SV1, SV2, SV3, SV4).
• Políticas estrictas de privacidad: Solo revelo detalles de un proyecto específico si coincide con el nombre de nuestro chat o grupo.

⚙️ *2. Ecosistema Google & APIs Publicitarias:*
• Análisis de rendimiento de campañas de adquisición mediante **Google Ads API**.
• Control de reputación y optimización de fichas en **Google Business Profile (Google Negocios) y Google Maps API**.
• Integraciones de Workspace: Lectura/envío de correos por **Gmail API**, auditoría de **Google Calendar** y sincronización de **Google Analytics 4 (GA4)**.

🔌 *3. Infraestructura Cloud y Dominios (Disponibilidad y Costos):*
• Consulta en tiempo real de disponibilidad de dominios.
• Comparación y cotización instantánea de costos de dominios entre **Cloudflare Registrar**, **OVH Cloud** y **Neubox**.
• Integración de precios netos con IVA incluido en MXN de **Syscom**.
• APIs integradas: OpenAI, Claude, DeepSeek, Twilio y Brevo SMTP.

📁 *4. Búsquedas Forenses y OSINT:*
• Acceso local y consultas ultrarrápidas sobre la Base de Datos DuckDB Soberana de personas (11 GB de registros históricos, CURP, RFC y domicilios).
• Rastreo OSINT pasivo de fuentes abiertas para números de teléfono y dorks públicos.

¿Qué tarea o consulta deseas que ejecutemos hoy?` });
          } else {
              enqueueCognitiveTask('mini', prompt, jid, chatName, isMaster);
          }
        }
        if (isOmega) {
          let prompt = text.replace(/^[\/\.]alexo/i, "").trim();
          if (!prompt) {
              await sock.sendMessage(jid, { text: "Por favor, proporciona la solicitud o tarea para Alex OMEGA." });
          } else {
              await sock.sendMessage(jid, { text: "⏳ Alex OMEGA está analizando la solicitud..." });
              enqueueCognitiveTask('omega', prompt, jid, chatName, isMaster);
          }
        }
        if (isAgy) {
          let prompt = text.replace(/^[\/\.](alexg|alex)/i, "").trim();
          if (!prompt) {
              await sock.sendMessage(jid, { text: "Por favor, proporciona la consulta para Alex Soberano." });
          } else {
              await sock.sendMessage(jid, { text: "⏳ Alex Soberano v13 está procesando esta consulta..." });
              enqueueCognitiveTask('agy', prompt, jid, chatName, isMaster);
          }
        }
        if (isLink) {
          let urlToDebrid = text.replace(/^[\/\.]alex link/i, "").trim();
          if (!urlToDebrid) {
              await sock.sendMessage(jid, { text: "Por favor, proporciona la URL que deseas desbridar. Ejemplo:\n.alex link https://rapidgator.net/file/..." });
          } else {
              // Reemplazo de compatibilidad para enlaces de MEGA
              urlToDebrid = urlToDebrid.replace(/mega\.nz/g, 'mega.co.nz');
              await sock.sendMessage(jid, { text: "⏳ Desbridando tu enlace con Mega-Debrid..." });
              try {
                const loginData = await curlRequest("https://www.mega-debrid.eu/api.php?action=connectUser&login=sercommx&password=39C3q1Ndrp9E");
                if (loginData.response_code === "ok") {
                  const token = loginData.token;
                  const debridData = await curlRequest(`https://www.mega-debrid.eu/api.php?action=getLink&token=${token}&link=${encodeURIComponent(urlToDebrid)}`);
                  const premiumLink = debridData.debridLink || debridData.link;
                  if (debridData.response_code === "ok" && premiumLink) {
                    await sock.sendMessage(jid, { text: `✅ *Enlace Premium Desbridado:* \n\n${premiumLink}` });
                  } else {
                    await sock.sendMessage(jid, { text: `❌ *Error al desbridar:* ${debridData.response_text || 'Enlace o hoster no soportado.'}` });
                  }
                } else {
                  await sock.sendMessage(jid, { text: `❌ *Error de autenticación:* ${loginData.response_text || 'Fallo al conectar con la cuenta.'}` });
                }
              } catch (err) {
                await sock.sendMessage(jid, { text: `❌ *Fallo de conexión:* ${err.message}` });
              }
          }
        }
        if (isDescargar) {
          let urlToDebrid = text.replace(/^[\/\.]alex descargar/i, "").trim();
          if (!urlToDebrid) {
              await sock.sendMessage(jid, { text: "Por favor, proporciona la URL del archivo a descargar. Ejemplo:\n.alex descargar https://rapidgator.net/file/..." });
          } else {
              // Reemplazo de compatibilidad para enlaces de MEGA
              urlToDebrid = urlToDebrid.replace(/mega\.nz/g, 'mega.co.nz');
              await sock.sendMessage(jid, { text: "⏳ Iniciando proceso de descarga en servidor y subida a Google Drive..." });
              try {
                const loginData = await curlRequest("https://www.mega-debrid.eu/api.php?action=connectUser&login=sercommx&password=39C3q1Ndrp9E");
                if (loginData.response_code === "ok") {
                  const token = loginData.token;
                  const debridData = await curlRequest(`https://www.mega-debrid.eu/api.php?action=getLink&token=${token}&link=${encodeURIComponent(urlToDebrid)}`);
                  const premiumLink = debridData.debridLink || debridData.link;
                  if (debridData.response_code === "ok" && premiumLink) {
                    await sock.sendMessage(jid, { text: `📥 *Enlace premium obtenido.* Descargando archivo temporalmente en el servidor...` });
                    handleDownloadAndUploadToDrive(premiumLink, jid);
                  } else {
                    await sock.sendMessage(jid, { text: `❌ *Error al desbridar:* ${debridData.response_text || 'Enlace no soportado.'}` });
                  }
                } else {
                  await sock.sendMessage(jid, { text: `❌ *Error de autenticación:* ${loginData.response_text || 'Fallo al conectar con la cuenta.'}` });
                }
              } catch (err) {
                await sock.sendMessage(jid, { text: `❌ *Fallo de conexión:* ${err.message}` });
              }
          }
        }
        if (isSoporte) {
          lastTechnicalSupportJid = jid; // Actualizar destinatario de notificaciones
          const parts = text.split(/\s+/);
          const subcmd = parts[2] ? parts[2].toLowerCase() : null;
          
          if (subcmd === 'cmd') {
            const id = parts[3];
            const cmdText = parts.slice(4).join(" ");
            
            if (!id || !cmdText) {
              await sock.sendMessage(jid, { text: "⚠️ *Uso incorrecto del comando:* \n\nEscribe: `.alex soporte cmd [ID] [comando]`\nEjemplo: `.alex soporte cmd 3045 Get-Process'" });
              return;
            }

            if (!activeSupportSessions[id] || (Date.now() - activeSupportSessions[id].lastSeen) > 25000) {
              await sock.sendMessage(jid, { text: `❌ El cliente con ID *${id}* no se encuentra en línea o la sesión expiró.` });
              return;
            }

            // Normalización para evitar bloqueos en comandos de GUI interactivos (calculadora, notepad, msinfo32, etc.)
            let parsedCmdText = cmdText;
            const guiApps = ['calc', 'notepad', 'msinfo32', 'mspaint', 'write', 'wordpad', 'taskmgr'];
            const lowerCmd = cmdText.trim().toLowerCase();
            if (guiApps.some(app => lowerCmd === app || lowerCmd.startsWith(app + ' '))) {
              // Envolver en Start-Process de PowerShell para que retorne al instante y no sea bloqueante
              parsedCmdText = `Start-Process ${cmdText}`;
            }

            const cmdId = "cmd_" + Date.now();
            await sock.sendMessage(jid, { text: `⏳ Enviando comando a la máquina del cliente *${id}*...` });

            // Encolar comando
            activeSupportSessions[id].queue.push({ id: cmdId, text: parsedCmdText });

            // Polling de respuesta en memoria (máximo 8 segundos)
            let attempts = 0;
            const interval = setInterval(async () => {
              attempts++;
              const res = activeSupportSessions[id]?.results[cmdId];
              if (res && res.status === 'done') {
                clearInterval(interval);
                await sock.sendMessage(jid, { text: `💻 *Resultado del comando (ID ${id}):*\n\`\`\`\n${res.output || '[Comando ejecutado sin salida]'}\n\`\`\`` });
                delete activeSupportSessions[id].results[cmdId]; // Limpiar memoria
              } else if (attempts >= 40) {
                clearInterval(interval);
                await sock.sendMessage(jid, { text: `⏳ *Comando en proceso:* El comando tomó más de 8 segundos. Se sigue ejecutando en segundo plano en la máquina del cliente.` });
              }
            }, 200);
            
          } else {
            await sock.sendMessage(jid, { text: "⏳ Buscando conexiones de soporte remoto activas en el servidor..." });
            
            // Listar puertos locales de túneles SSH (rango 30XX)
            exec("ss -tln -p 2>/dev/null | grep -E '127.0.0.1:30[0-9]{2}' || true", async (err, stdout) => {
              let responseText = "🖥️ *Clientes de Soporte Remoto Activos en SV1:*\n\n";
              let foundAny = false;
              
              // 1. Añadir clientes SSH (Túnel)
              if (!err && stdout) {
                const lines = stdout.split('\n').filter(line => line.trim() !== "");
                const portsFound = [];
                lines.forEach(line => {
                  const match = line.match(/:30([0-9]{2})/);
                  if (match) {
                    const port = "30" + match[1];
                    if (!portsFound.includes(port)) {
                      portsFound.push(port);
                      foundAny = true;
                      responseText += `🔌 *ID (Túnel SSH):* \`${port}\`\n👉 Técnico: \`ssh -p ${port} SercomSoporte@localhost\` (desde SV1)\n\n`;
                    }
                  }
                });
              }
              
              // 2. Añadir clientes Agente HTTP/Socks
              const now = Date.now();
              Object.keys(activeSupportSessions).forEach(id => {
                if ((now - activeSupportSessions[id].lastSeen) < 25000) {
                  foundAny = true;
                  responseText += `⚡ *ID (Agente HTTP):* \`${id}\`\n👉 Equipo: \`${activeSupportSessions[id].hostname}\`\n👉 Ejecutar comando: \`.alex soporte cmd ${id} [comando]\`\n\n`;
                }
              });
              
              if (!foundAny) {
                await sock.sendMessage(jid, { text: "📭 *No hay clientes de soporte remoto conectados* en este momento." });
              } else {
                responseText += "⚠️ Recuerda cerrar el script en la máquina del cliente al terminar para limpiar credenciales.";
                await sock.sendMessage(jid, { text: responseText });
              }
            });
          }
        }
      }
    }
  });

  sock.ev.on('messaging-history.set', async ({ messages, chats }) => {
    connectionStatus = 'SYNCHRONIZING';
    console.log(`\n[HISTORIAL] Procesando ${messages.length} mensajes en SQLite...`);
    
    const groupedMessages = {};
    messages.forEach(msg => {
      const jid = msg.key?.remoteJid;
      if (jid) {
        if (!groupedMessages[jid]) groupedMessages[jid] = [];
        groupedMessages[jid].push(msg);
      }
    });

    const totalChats = Object.keys(groupedMessages).length;
    let processedChats = 0;
    let totalAddedMessages = 0;

    for (const [jid, msgs] of Object.entries(groupedMessages)) {
      const chatMeta = chats.find(c => c.id === jid);
      const isArchived = chatMeta?.archive === true || chatsIndex[jid]?.archive === true;
      if (isArchived) {
        processedChats++;
        continue;
      }
      
      const name = chatMeta?.name || jid;
      const added = saveChatHistoryToDatabase(jid, name, msgs);
      totalAddedMessages += added;
      processedChats++;
      drawProgressBar(processedChats, totalChats, `chats | Inyectados: +${totalAddedMessages} msgs nuevos`);
    }

    console.log('\n[HISTORIAL] Sincronización y persistencia en base de datos completada.');
    connectionStatus = 'CONNECTED';
  });
}

// === ENDPOINTS API ===
app.get('/status', (req, res) => {
  res.json({ status: connectionStatus });
});

app.get('/chats', (req, res) => {
  const allChats = Object.values(chatsIndex);
  res.json(allChats.sort((a, b) => b.timestamp - a.timestamp));
});

app.get('/chats/:jid/messages', (req, res) => {
  const jid = formatJid(req.params.jid);
  db.all(`SELECT * FROM messages WHERE jid = ? ORDER BY timestamp ASC`, [jid], (err, rows) => {
    if (err) return res.status(500).json({ error: err.message });
    res.json(rows);
  });
});

app.post('/send', async (req, res) => {
  const { to, message } = req.body;
  if (!to || !message) return res.status(400).json({ error: 'Faltan parámetros: "to" o "message"' });
  if (connectionStatus !== 'CONNECTED') return res.status(503).json({ error: 'Bot no conectado' });

  try {
    const formattedJid = formatJid(to);
    const sentMsg = await sock.sendMessage(formattedJid, { text: message });
    saveChatHistoryToDatabase(formattedJid, chatsIndex[formattedJid]?.name || to, [sentMsg]);
    res.json({ success: true, messageId: sentMsg.key.id });
  } catch (error) {
    res.status(500).json({ error: error.message });
  }
});

// === SISTEMA DE SOPORTE INTERACTIVO INDEPENDIENTE ===
const SERCOM_API_KEY   = "SrC0mS0p0rt3#S3cur1tyKey#2026";
const AGENT_VERSION    = "v3.4.1"; // incrementar con cada release del agente
const SERCOM_AGENT_TOKEN = "SercomAgentToken2026SecureHashKey";

const activeSupportSessions = {};
// Sesiones de soporte persistidas en SQLite (sobreviven reinicios)
const supportSessions = {}; // cache en memoria, cargado desde SQLite
db.run(`CREATE TABLE IF NOT EXISTS soporte_sessions (
  token TEXT PRIMARY KEY,
  created_at INTEGER NOT NULL
)`, () => {
  // Cargar sesiones vigentes (< 8h) al arrancar
  const cutoff = Date.now() - 8 * 60 * 60 * 1000;
  db.all(`SELECT token, created_at FROM soporte_sessions WHERE created_at > ?`, [cutoff], (err, rows) => {
    if (!err && rows) {
      rows.forEach(r => { supportSessions[r.token] = r.created_at; });
      console.log(`[SOPORTE] ${rows.length} sesión(es) de técnico restaurada(s) desde SQLite`);
    }
  });
});
let lastTechnicalSupportJid = '18627474530380@s.whatsapp.net';

function requireSupportAuth(req, res, next) {
  const token = req.signedCookies ? req.signedCookies.soporte_session : null;
  if (token && supportSessions[token]) {
    // Renovar sesión activa
    const now = Date.now();
    supportSessions[token] = now;
    db.run(`UPDATE soporte_sessions SET created_at = ? WHERE token = ?`, [now, token]);
    return next();
  }
  res.status(401).json({ error: 'Sesión inválida o expirada' });
}

app.post('/soporte/register', async (req, res) => {
  // Validar cabecera de autenticación del agente
  const agentToken = req.headers['x-sercom-agent-token'];
  if (agentToken !== SERCOM_AGENT_TOKEN) {
    return res.status(401).json({ error: 'Acceso no autorizado' });
  }

  const { id, hostname, health } = req.body;
  if (!id) return res.status(400).json({ error: 'Falta parámetro: id' });
  
  activeSupportSessions[id] = {
    hostname: hostname || 'Desconocido',
    queue: [],
    results: {},
    lastSeen: Date.now(),
    health: health || null
  };
  
  console.log(`[SOPORTE] Agente registrado: ID ${id} (${hostname})`);
  
  // Si hay un reporte de salud, formatear y enviar por WhatsApp al técnico
  if (health && sock && connectionStatus === 'CONNECTED') {
    try {
      const h = typeof health === 'string' ? JSON.parse(health) : health;
      let msgText = `🖥️ *Nuevo Cliente de Soporte Conectado (ID: ${id})*\n\n`;
      
      if (h.Hardware) {
        const sysType = h.Hardware.PCSystemType === 2 ? 'Laptop' : 'PC Escritorio';
        msgText += `*Equipo:* ${h.Hardware.Manufacturer || ''} ${h.Hardware.Model || ''} (${sysType})\n`;
        msgText += `*Placa Madre:* ${h.Hardware.BoardManufacturer || ''} ${h.Hardware.BoardModel || ''}\n`;
        msgText += `*BIOS:* ${h.Hardware.BIOSVersion || ''} (Modo: ${h.Hardware.FirmwareType || ''} | Secure Boot: ${h.Hardware.SecureBoot || ''})\n`;
        msgText += `*CPU:* ${h.Hardware.CPU || ''} (Carga: ${h.Hardware.CPULoad || 0}%)\n`;
        
        // Reportar batería si es Laptop y tiene datos
        if (h.Hardware.Battery && h.Hardware.Battery.IsLaptop) {
          const bat = h.Hardware.Battery;
          msgText += `*Salud de Batería:* ${bat.HealthPercent}% ${bat.HealthPercent < 70 ? '⚠️ *[Bateria Desgastada - Sugiere Reemplazo]*' : '✅'} (Actual: ${bat.FullChargedCapacity} mWh / Diseño: ${bat.DesignCapacity} mWh)\n`;
        }
      }
      
      if (h.RAM && h.RAM.length > 0) {
        const totalRam = h.RAM.reduce((sum, item) => sum + (item.CapacityGB || 0), 0);
        const moduleDetails = h.RAM.map(m => `${m.CapacityGB}GB ${m.Speed || ''}MHz (${m.Manufacturer || 'S/N'})`).join(' + ');
        msgText += `*RAM Total:* ${totalRam} GB [${moduleDetails}]\n`;
      }
      
      if (h.TemperatureC !== undefined && h.TemperatureC !== null && h.TemperatureC !== -1) {
        const temp = h.TemperatureC;
        msgText += `*Temperatura CPU:* \`${temp}°C\` ${temp >= 75 ? '⚠️ *[Alerta Térmica - Requiere Limpieza]*' : '✅'}\n`;
      }
      
      if (h.Disks && h.Disks.length > 0) {
        msgText += `*Almacenamiento:* \n`;
        h.Disks.forEach(d => {
          const vols = d.Volumes ? d.Volumes.map(v => `${v.Letter} [Libre: ${v.FreeGB}GB / ${v.SizeGB}GB]`).join(', ') : '';
          msgText += `  - ${d.Model || 'Disco'} (${d.SizeGB} GB) [SMART: ${d.SMART || 'OK'}] - ${vols}\n`;
        });
      }
      
      if (h.Network && h.Network.length > 0) {
        const net = h.Network[0];
        msgText += `*Conexion:* Conectado por ${net.Type || 'Red'} (${net.SpeedMbps || ''} Mbps) | Latencia DNS: ${h.PingLatencyMs || 0}ms\n`;
      }
      
      if (h.Services) {
        msgText += `*Servicios Criticos:* \n`;
        Object.keys(h.Services).forEach(s => {
          const status = h.Services[s].Status;
          if (status !== 'NotInstalled') {
            msgText += `  - ${s}: ${status === 'Running' ? '✅ Running' : '⚠️ ' + status}\n`;
          }
        });
      }
      
      if (h.Events && h.Events.length > 0) {
        const errors = h.Events.filter(e => e.Message && (e.Message.toLowerCase().includes('error') || e.Message.toLowerCase().includes('fail'))).length;
        msgText += `*Historial de Eventos:* ${errors} alertas de error detectadas en los ultimos 20 logs.\n`;
      }
      
      msgText += `\n*Para ejecutar comandos:* \`.alex soporte cmd ${id} [comando]\``;
      
      await sock.sendMessage(lastTechnicalSupportJid, { text: msgText });
    } catch (err) {
      console.error('[SOPORTE] Error al formatear o enviar el diagnóstico por WhatsApp:', err);
    }
  }
  
  res.json({ success: true });
});

app.get('/soporte/poll', (req, res) => {
  // Validar cabecera de autenticación del agente
  const agentToken = req.headers['x-sercom-agent-token'];
  if (agentToken !== SERCOM_AGENT_TOKEN) {
    return res.status(401).json({ error: 'Acceso no autorizado' });
  }

  const { id } = req.query;
  if (!id || !activeSupportSessions[id]) return res.status(404).json({ error: 'Sesión no encontrada' });
  
  activeSupportSessions[id].lastSeen = Date.now();
  const nextCmd = activeSupportSessions[id].queue.shift();
  if (nextCmd) {
    res.json({ command: nextCmd });
  } else {
    res.json({ command: null });
  }
});

app.post('/soporte/response', (req, res) => {
  // Validar cabecera de autenticación del agente
  const agentToken = req.headers['x-sercom-agent-token'];
  if (agentToken !== SERCOM_AGENT_TOKEN) {
    return res.status(401).json({ error: 'Acceso no autorizado' });
  }

  const { id, cmdId, output } = req.body;
  if (!id || !cmdId) return res.status(400).json({ error: 'Faltan parámetros' });
  
  if (activeSupportSessions[id]) {
    activeSupportSessions[id].lastSeen = Date.now();
    activeSupportSessions[id].results[cmdId] = { status: 'done', output: output || '' };
    res.json({ success: true });
  } else {
    res.status(404).json({ error: 'Sesión no encontrada' });
  }
});

app.post('/soporte/cmd', async (req, res) => {
  // Validar clave de API O sesión de cookie HttpOnly firmada para control de comandos
  const apiKey = req.headers['x-sercom-api-key'];
  const sessionToken = req.signedCookies ? req.signedCookies.soporte_session : null;

  if (apiKey !== SERCOM_API_KEY && (!sessionToken || !supportSessions[sessionToken])) {
    return res.status(401).json({ error: 'Acceso no autorizado o sesión expirada' });
  }

  const { id, cmd } = req.body;
  if (!id || !cmd) return res.status(400).json({ error: 'Faltan parametros: id o cmd' });
  if (!activeSupportSessions[id] || (Date.now() - activeSupportSessions[id].lastSeen) > 60000) {
    return res.status(404).json({ error: 'Cliente desconectado o sesion expirada' });
  }

  const cmdId = "cmd_api_" + Date.now();
  activeSupportSessions[id].queue.push({ id: cmdId, text: cmd });

  let attempts = 0;
  const interval = setInterval(() => {
    attempts++;
    const result = activeSupportSessions[id]?.results[cmdId];
    if (result && result.status === 'done') {
      clearInterval(interval);
      res.json({ output: result.output });
      delete activeSupportSessions[id].results[cmdId];
    } else if (attempts >= 20) {
      clearInterval(interval);
      res.status(504).json({ error: 'Timeout de respuesta en el cliente remoto' });
    }
  }, 400);
});

app.post('/soporte/login', async (req, res) => {
  const { user, pass, turnstileToken } = req.body;
  if (!user || !pass || !turnstileToken) {
    return res.status(400).json({ error: 'Faltan parámetros de credenciales o de captcha' });
  }

  if (user !== 'supersercom' || pass !== 'SrC0mS0p0rt3#S3cur1tyKey#2026') {
    return res.status(401).json({ error: 'Usuario o contraseña inválidos' });
  }

  try {
    const secretKey = '0x4AAAAAACGkJx_mcwf_n3GYitdQLBuF72E';
    const verifyUrl = 'https://challenges.cloudflare.com/turnstile/v0/siteverify';
    
    const response = await fetch(verifyUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: `secret=${encodeURIComponent(secretKey)}&response=${encodeURIComponent(turnstileToken)}&remoteip=${encodeURIComponent(req.ip)}`
    });
    
    const outcome = await response.json();
    if (outcome.success) {
      // Generar sesión segura y guardarla en cookies HttpOnly
      const sessionToken = Math.random().toString(36).substring(2) + Date.now().toString(36);
      const sessionTs = Date.now();
      supportSessions[sessionToken] = sessionTs;
      db.run(`INSERT OR REPLACE INTO soporte_sessions (token, created_at) VALUES (?, ?)`, [sessionToken, sessionTs]);

      res.cookie('soporte_session', sessionToken, {
        httpOnly: true,
        secure: true,
        signed: true,
        sameSite: 'lax',
        maxAge: 8 * 60 * 60 * 1000 // 8 horas
      });

      res.json({ success: true });
    } else {
      res.status(403).json({ error: 'Validación de captcha inválida o expirada' });
    }
  } catch (err) {
    res.status(500).json({ error: 'Error interno al verificar Turnstile: ' + err.message });
  }
});

app.get('/soporte/agentes', requireSupportAuth, (req, res) => {
  // Retornar solo agentes activos en los últimos 60 segundos
  const now = Date.now();
  const activeAgents = {};
  for (const [id, session] of Object.entries(activeSupportSessions)) {
    if (now - session.lastSeen <= 60000) {
      activeAgents[id] = {
        id: session.id,
        hostname: session.hostname,
        health: session.health
      };
    }
  }
  res.json(activeAgents);
});

app.get('/soporte/health', requireSupportAuth, (req, res) => {
  const { id } = req.query;
  if (!id || !activeSupportSessions[id]) {
    return res.status(404).json({ error: 'Agente no encontrado o inactivo' });
  }
  res.json({
    hostname: activeSupportSessions[id].hostname,
    health: activeSupportSessions[id].health
  });
});

app.get('/soporte/download/gui-src', (req, res) => {
  try {
    const srcPath = '/home/alex/alex_omega/whatsapp_sovereign/SoporteRemotoGUI.cs';
    const logoPath = '/home/alex/alex_omega/whatsapp_sovereign/logo-texto-blanco.png';
    const iconPath = '/home/alex/alex_omega/whatsapp_sovereign/favicon.ico';
    
    let code = fs.readFileSync(srcPath, 'utf-8');

    // Inyectar versión actual del agente
    code = code.replace('"##AGENT_VERSION##"', `"${AGENT_VERSION}"`);
    
    // Inyectar logotipo en base64
    if (fs.existsSync(logoPath)) {
      const logoBase64 = fs.readFileSync(logoPath).toString('base64');
      code = code.replace('private static readonly string LogoBase64 = "";', `private static readonly string LogoBase64 = "${logoBase64}";`);
    }
    
    // Inyectar icono en base64
    if (fs.existsSync(iconPath)) {
      const iconBase64 = fs.readFileSync(iconPath).toString('base64');
      code = code.replace('private static readonly string IconBase64 = "";', `private static readonly string IconBase64 = "${iconBase64}";`);
    }
    
    res.setHeader('Content-Type', 'text/plain; charset=utf-8');
    res.send(code);
  } catch (err) {
    res.status(500).send(`Error: ${err.message}`);
  }
});

// ── Versión actual del agente (consultada por el cliente para auto-update) ────
app.get('/soporte/version', (req, res) => {
  res.json({
    version: AGENT_VERSION,
    downloadUrl: '/soporte/download/gui-src',
    changelog: 'Mejoras de estabilidad y nuevas funciones de soporte remoto'
  });
});

// Alias para cualquier versión numerada del código fuente del agente
app.get('/soporte/download/gui-src-v:ver', (req, res) => res.redirect('/soporte/download/gui-src'));

app.get('/soporte/download/logo', (req, res) => {
  res.sendFile('/home/alex/alex_omega/whatsapp_sovereign/logo-texto-blanco.webp', (err) => {
    if (err) {
      console.error("ERROR AL ENVIAR LOGO:", err);
      res.status(500).send(err.message);
    }
  });
});

app.get('/soporte/download/favicon', (req, res) => {
  res.sendFile('/home/alex/alex_omega/whatsapp_sovereign/favicon.ico');
});

// Servir panel estático en la raíz para soporte.sercommx.com deshabilitando caché de forma estricta
app.get('/', (req, res) => {
  res.setHeader('Cache-Control', 'no-store, no-cache, must-revalidate, private');
  res.sendFile('/home/alex/alex_omega/whatsapp_sovereign/panel/index.html');
});
app.use('/', express.static('/home/alex/alex_omega/whatsapp_sovereign/panel'));

const server = http.createServer(app);
startRelayServer(server);

server.listen(PORT, () => {
  console.log(`Iniciando servidor de API y WebSocket Relay en puerto ${PORT}...`);
  connectToWhatsApp().catch(err => console.error('Error al iniciar WhatsApp:', err));
});
