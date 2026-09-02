import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/** Root of the checkout. Claude Code sets CLAUDE_PROJECT_DIR; cwd is the fallback. */
export const projectDir = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();

/** Read the hook payload that Claude Code writes to stdin. */
export async function readPayload() {
  const raw = await new Promise((resolve) => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => (data += chunk));
    process.stdin.on('end', () => resolve(data));
    // If nothing is piped in, don't hang the hook.
    setTimeout(() => resolve(data), 4000).unref?.();
  });

  try {
    return JSON.parse(raw || '{}');
  } catch {
    return {};
  }
}

/**
 * Block the tool call. Exit code 2 is the documented "deny" signal; whatever we
 * write to stderr is fed back to the model as the reason.
 */
export function block(reason) {
  console.error(reason);
  process.exit(2);
}

/** Allow the tool call. */
export function allow() {
  process.exit(0);
}

/** The shell command a Bash / PowerShell tool call is about to run. */
export function commandOf(payload) {
  return payload?.tool_input?.command ?? '';
}

export function statePath(...parts) {
  return join(projectDir, '.claude', 'state', ...parts);
}

export function readJsonIfExists(path) {
  if (!existsSync(path)) return null;
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    return null;
  }
}

/**
 * Strip quoted strings before scanning a command for dangerous patterns, so that
 * a message mentioning "--force" is not mistaken for an actual force push.
 */
export function withoutQuotedStrings(command) {
  return command
    .replace(/"(?:[^"\\]|\\.)*"/g, '""')
    .replace(/'(?:[^'\\]|\\.)*'/g, "''")
    .replace(/@'[\s\S]*?'@/g, "''")
    .replace(/@"[\s\S]*?"@/g, '""');
}

/** Split a compound shell command into individually inspectable segments. */
export function segments(command) {
  return withoutQuotedStrings(command)
    .split(/&&|\|\||;|\||\n/g)
    .map((s) => s.trim())
    .filter(Boolean);
}
