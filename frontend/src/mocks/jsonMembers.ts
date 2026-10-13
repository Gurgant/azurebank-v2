/**
 * The members of a JSON object in the order the text writes them, each one as many times as it
 * is written.
 *
 * `JSON.parse` cannot answer that: it keeps the last value of a member written twice and says
 * nothing of the others. The API reads a body member by member and refuses the second occurrence
 * of one it knows, so a handler that has to refuse it too needs the text, not the object.
 *
 * Only the object's own members are listed. A value is handed over as `JSON.parse` reads it, so
 * inside a value a member written twice is still folded to its last one. That is as far as the
 * API looks as well, on the bodies measured: it skips the value of a member it does not know
 * without reading it.
 *
 * The text must already have been read by `JSON.parse` as an object. What is walked here is
 * therefore well formed, and nothing below checks that it is.
 */
export function jsonMembersAsWritten(raw: string): [name: string, value: unknown][] {
  const endOfString = (from: number): number => {
    let at = from + 1;
    while (raw[at] !== '"') at += raw[at] === '\\' ? 2 : 1;
    return at + 1;
  };

  const endOfValue = (from: number): number => {
    const first = raw[from];
    if (first === '"') return endOfString(from);
    let at = from;
    if (first !== '{' && first !== '[') {
      // A number, `true`, `false` or `null`: it ends where the object goes on.
      while (!/[\s,}]/.test(raw[at])) at++;
      return at;
    }
    let depth = 0;
    do {
      const char = raw[at];
      if (char === '"') {
        // A bracket inside a string opens and closes nothing.
        at = endOfString(at);
        continue;
      }
      if (char === '{' || char === '[') depth++;
      if (char === '}' || char === ']') depth--;
      at++;
    } while (depth > 0);
    return at;
  };

  const members: [string, unknown][] = [];
  let at = raw.indexOf('{') + 1;
  while (raw[at] !== '}') {
    if (raw[at] !== '"') {
      // White space, or the comma between two members.
      at++;
      continue;
    }
    const nameEnd = endOfString(at);
    // Parsed and not sliced: a name may be written with escapes, and `"name"` is `name`.
    const name = JSON.parse(raw.slice(at, nameEnd)) as string;
    at = raw.indexOf(':', nameEnd) + 1;
    while (/\s/.test(raw[at])) at++;
    const valueEnd = endOfValue(at);
    members.push([name, JSON.parse(raw.slice(at, valueEnd))]);
    at = valueEnd;
  }
  return members;
}
