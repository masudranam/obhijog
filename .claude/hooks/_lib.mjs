import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/** Root of the checkout. Claude Code sets CLAUDE_PROJECT_DIR; cwd is the fallback. */
export const projectDir = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();

/**
 * Read the hook payload that Claude Code writes to stdin.
 *
 * Returns `{}` when nothing was piped in — a legitimate case some invocations hit.
 * Returns `{ unparseable: <excerpt> }` when there WAS input but it was not JSON, so
 * a caller that enforces something can fail closed instead of waving the call
 * through. A guard that cannot read its input must not allow: that is how a gate
 * becomes decoration.
 *
 * The `.trim()` is load-bearing, not cosmetic. Piping a payload from PowerShell
 * prepends a U+FEFF byte-order mark; `JSON.parse` throws on it, and the previous
 * version of this function swallowed that and returned `{}` — which made every guard
 * allow every command. `trim()` removes it because U+FEFF counts as whitespace per
 * the language spec. Do not drop it, and do not "simplify" it to a plain parse.
 */
export async function readPayload() {
  const raw = await new Promise((resolve) => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => (data += chunk));
    process.stdin.on('end', () => resolve(data));
    // If nothing is piped in, don't hang the hook.
    setTimeout(() => resolve(data), 4000).unref?.();
  });

  const text = raw.trim();
  if (!text) return {};

  try {
    return JSON.parse(text);
  } catch {
    return { unparseable: text.slice(0, 200) };
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

/**
 * Commands whose stdin IS code. A heredoc feeding one of these is executed, so its
 * body stays visible to the guards below; a heredoc feeding anything else is data
 * being piped somewhere and cannot run.
 *
 * `xargs` is here because it turns its stdin into arguments for another command,
 * which is execution by a different route.
 */
const INTERPRETERS =
  /\b(?:sh|bash|zsh|ksh|dash|fish|python[0-9.]*|node|deno|ruby|perl|php|pwsh|powershell|cmd|eval|exec|source|xargs)\b/i;

/**
 * Remove bash heredoc bodies, which are data rather than commands.
 *
 * Without this, prose about the harness cannot be written through a heredoc: filing
 * issue #16 was itself blocked because its body described the hook-skipping flag, and
 * the guard saw the word in the command string. Quoted strings were already stripped
 * for exactly this reason; a heredoc is the same thing with different punctuation.
 *
 * The interpreter check is what keeps this from being a hole. `gh issue create <<EOF`
 * pipes text into an HTTP request and the body can never execute; `bash <<EOF` runs
 * every line of it, so that body is left where the guards can see it.
 */
export function withoutHeredocs(command) {
  const lines = command.split('\n');
  const kept = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];
    kept.push(line);
    i++;

    // `(?<!<)` and `(?!<)` exclude `<<<`, the herestring. It has no terminator, so
    // treating it as a heredoc opener sent the scan below off the end of the command
    // and deleted every remaining line — `grep x <<< "y"` on one line turned a real
    // `git push --force` on the next into something no guard could see.
    const opened = [
      ...line.matchAll(/(?<!<)<<(?!<)-?\s*(?:'([^']+)'|"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))/g),
    ];

    if (opened.length === 0 || INTERPRETERS.test(line)) continue;

    // Several heredocs can open on one line; their bodies arrive in that order.
    for (const open of opened) {
      const delimiter = open[1] ?? open[2] ?? open[3];

      let end = i;
      while (end < lines.length && lines[end].trim() !== delimiter) end++;

      // No terminator anywhere: this is not a heredoc we understand, so keep the
      // lines. Dropping them is the dangerous direction — a mistyped delimiter would
      // silently switch every guard off for the rest of the command.
      if (end >= lines.length) break;

      i = end + 1; // skip the body and the terminator
    }
  }

  return kept.join('\n');
}

/** Split a compound shell command into individually inspectable segments. */
export function segments(command) {
  return withoutQuotedStrings(withoutHeredocs(command))
    .split(/&&|\|\||;|\||\n/g)
    .map((s) => s.trim())
    .filter(Boolean);
}

/**
 * The arguments of a segment that **invokes** `program`, or `null` when it does not.
 *
 * The guards used to match `/\bgit\b[\s\S]*--no-verify/` — the word "git" anywhere,
 * then the flag anywhere after it. That is true of an ordinary English sentence about
 * skipping git hooks, which is how filing #16 came to be blocked by the thing it was
 * about. Requiring `git` to be the command word makes a sentence unmatchable while
 * leaving every real invocation matched.
 *
 * Three kinds of prefix are stepped over, because each is an ordinary way to reach
 * the same command and none of them is an attempt to hide it:
 *
 *   · shell control words — `if`, `then`, `do`, `else`, `{`, `(`. `segments` splits on
 *     `;`, so `if true; then gh pr merge 9; fi` arrives here as `then gh pr merge 9`.
 *     Without this the **merge gate itself** was bypassable by wrapping the merge in
 *     an `if`, which review found before this shipped.
 *   · environment assignments, and wrappers like `sudo`, `env`, `command`, `nohup`,
 *     `time`, `timeout 30`.
 *   · an absolute or relative path — `/usr/bin/git`.
 *
 * **The narrowing this accepts:** `xargs git push --force` is still not seen, because
 * the command word is `xargs` and what follows is data to it rather than a command
 * here. That is deliberate. These guards exist so a rule the agent was told once
 * still holds on the four hundredth command — not to withstand an agent deliberately
 * routing around them, which nothing here could do anyway.
 */
// Punctuation needs no space after it — `(git push` — where a keyword does, or `dogit`
// would read as `do git`.
const CONTROL = String.raw`(?:[({&!]\s*|(?:if|then|elif|else|do|while|until)\s+)`;
const WRAPPER = String.raw`(?:sudo|env|command|nohup|exec|time|timeout\s+\S+)`;

export function argvFor(segment, program) {
  const invocation = new RegExp(
    String.raw`^(?:\s*${CONTROL})*` +
      String.raw`(?:[A-Za-z_]\w*=\S*\s+)*` +
      String.raw`(?:${WRAPPER}\s+(?:-\S+\s+)*)*` +
      String.raw`(?:[\w.\-]*[/\\])*${program}(?:\.exe)?\b`,
    'i',
  );

  const match = invocation.exec(segment);
  if (!match) return null;

  return segment.slice(match[0].length).trim().split(/\s+/).filter(Boolean);
}

/**
 * The bare name of a git ref: `refs/heads/main` is `main`.
 *
 * The rules that protect `main` used to match it with `\b(main|master)\b`, which found
 * it inside a path. Comparing argv tokens exactly lost that, so
 * `git push --delete origin refs/heads/main` slipped through — a regression review
 * caught. Both spellings name the same branch and both must be refused.
 */
export function refName(token) {
  return token.split(/[/\\]/).pop();
}

/**
 * True when `argv` carries this short flag, including inside a bundle — `-fu` is
 * `-f -u`. The previous `(?:^|\s)-f(?:\s|$)` regex missed the bundled form.
 */
export function hasShortFlag(argv, letter) {
  return argv.some((token) => new RegExp(`^-[A-Za-z]*${letter}[A-Za-z]*$`).test(token));
}
