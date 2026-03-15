const { spawn } = require('child_process');
const { log_mcp_usage } = require('./cursor-usage-logger/src/index');

function callMcpTool(server, toolName, args) {
  return new Promise((resolve, reject) => {
    const mcpProcess = spawn('node', ['-e', `
      const { ${toolName} } = require('${server}');
      ${toolName}(${JSON.stringify(args)}).then(console.log).catch(console.error);
    `]);

    let output = '';
    mcpProcess.stdout.on('data', (data) => {
      output += data.toString();
    });

    mcpProcess.stderr.on('data', (data) => {
      console.error(`stderr: ${data}`);
    });

    mcpProcess.on('close', (code) => {
      if (code !== 0) {
        return reject(new Error(`Process exited with code ${code}`));
      }
      resolve(JSON.parse(output));
    });
  });
}

async function callMcpToolWithLogging(server, toolName, args) {
  const inputTokens = JSON.stringify(args).length;
  
  const startTime = Date.now();
  const result = await callMcpTool(server, toolName, args);
  const durationMs = Date.now() - startTime;
  
  const outputTokens = JSON.stringify(result).length;
  
  await log_mcp_usage({
    server,
    tool_name: toolName,
    request_count: 1,
    input_tokens: inputTokens,
    output_tokens: outputTokens,
    duration_ms: durationMs,
    has_error: false,
  });
  
  return result;
}

module.exports = { callMcpToolWithLogging };
