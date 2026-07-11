import http from 'node:http';
import https from 'node:https';

const listenHost = process.env.BP_PROXY_HOST || '127.0.0.1';
const listenPort = Number(process.env.BP_PROXY_PORT || 6970);
const upstreamHost = process.env.BP_UPSTREAM_HOST || '127.0.0.1';
const upstreamPort = Number(process.env.BP_UPSTREAM_PORT || 6969);

const server = http.createServer((request, response) => {
    const headers = { ...request.headers, host: `${upstreamHost}:${upstreamPort}` };
    const upstream = https.request({
        hostname: upstreamHost,
        port: upstreamPort,
        path: request.url,
        method: request.method,
        headers,
        rejectUnauthorized: false,
    }, upstreamResponse => {
        const contentType = String(upstreamResponse.headers['content-type'] || '');
        if (!contentType.includes('text/html')) {
            response.writeHead(upstreamResponse.statusCode || 502, {
                ...upstreamResponse.headers,
                'cache-control': 'no-store',
            });
            upstreamResponse.pipe(response);
            return;
        }

        const chunks = [];
        upstreamResponse.on('data', chunk => chunks.push(chunk));
        upstreamResponse.on('end', () => {
            const html = Buffer.concat(chunks)
                .toString('utf8')
                .replace(/\s*<link[^>]+href=["']https:\/\/fonts\.(?:googleapis|gstatic)\.com[^>]*>/gi, '');
            const responseHeaders = { ...upstreamResponse.headers };
            delete responseHeaders['content-length'];
            responseHeaders['content-length'] = Buffer.byteLength(html);
            responseHeaders['cache-control'] = 'no-store';
            response.writeHead(upstreamResponse.statusCode || 502, responseHeaders);
            response.end(html);
        });
    });

    upstream.on('error', error => {
        if (!response.headersSent) response.writeHead(502, { 'Content-Type': 'text/plain; charset=utf-8' });
        response.end(`Local test proxy error: ${error.message}`);
    });
    request.pipe(upstream);
});

server.listen(listenPort, listenHost, () => {
    console.log(`BattlePass local test proxy listening on http://${listenHost}:${listenPort}`);
});
