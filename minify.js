import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const inputPath = path.join(__dirname, 'panel', 'index.html');
const outputPath = path.join(__dirname, 'panel', 'index.min.html');

console.log('=== Iniciando Minificación y Ofuscación de SercomDesk Panel ===');

try {
  let html = fs.readFileSync(inputPath, 'utf8');

  // Extract the main script block
  const scriptRegex = /<script\b[^>]*>([\s\S]*?)<\/script>/;
  const match = html.match(scriptRegex);

  if (match) {
    let js = match[1];

    // 1. Remover comentarios de JS (multilínea y unilineal)
    js = js.replace(/\/\*[\s\S]*?\*\//g, '');
    js = js.replace(/\/\/.*$/gm, '');

    // 2. Ofuscación de variables y funciones internas para dificultar ingeniería inversa
    const obfuscationMap = {
      'authenticated': '_secAuth',
      'activeWs': '_secWs',
      'currentAgentId': '_secAgentId',
      'attemptLogin': '_secAttemptLogin',
      'logout': '_secLogout',
      'fetchAgents': '_secFetchAgents',
      'renderGrid': '_secRenderGrid',
      'sendQuickCmd': '_secSendQuickCmd',
      'takeControl': '_secTakeControl',
      'startStream': '_secStartStream',
      'requestElevation': '_secRequestElevation',
      'closeSession': '_secCloseSession',
      'startMonitoring': '_secStartMonitoring'
    };

    for (const [original, replacement] of Object.entries(obfuscationMap)) {
      const regex = new RegExp(`\\b${original}\\b`, 'g');
      js = js.replace(regex, replacement);
      html = html.replace(new RegExp(`\\b${original}\\b`, 'g'), replacement);
    }

    // 3. Compactar espacios en blanco y líneas vacías en el código JavaScript
    js = js.split('\n')
      .map(line => line.trim())
      .filter(line => line.length > 0)
      .join(' ');

    // Reinyectar el JavaScript minificado
    html = html.replace(scriptRegex, `<script>${js}</script>`);
  }

  // 4. Minificación básica del HTML (remover espacios múltiples y saltos de línea innecesarios fuera de áreas pre/textarea)
  html = html.split('\n')
    .map(line => line.trim())
    .filter(line => line.length > 0)
    .join('\n');

  fs.writeFileSync(outputPath, html, 'utf8');
  console.log('✅ Minificación completada con éxito.');
  console.log(`Archivo de salida generado en: ${outputPath}`);

} catch (err) {
  console.error('❌ Error durante la minificación:', err.message);
}
