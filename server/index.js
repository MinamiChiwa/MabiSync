import { createRoomServer } from './room-server.js';

const port = Number(process.env.MABISYNC_PORT ?? 3132);
const host = process.env.MABISYNC_HOST ?? '127.0.0.1';
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid MABISYNC_PORT');
const service = await createRoomServer({ port, host });
console.log(JSON.stringify({ product: 'MabiSync', protocolVersion: 1, serverEpoch: service.serverEpoch, port: service.port, host }));
let closing = false;
async function shutdown() { if (!closing) { closing = true; await service.close(); } }
process.once('SIGINT', shutdown);
process.once('SIGTERM', shutdown);
