import { readFileSync } from 'node:fs';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';

/**
 * Each `as ZodType<T>` cast in responseSchemas.ts tells the compiler that a generated schema
 * parses the `T` openapi-typescript wrote for the same answer, and the compiler takes its word.
 * The word is kept by `_GeneratedSchemasMatchSpec` in the same file: `tsc -b` stops the build when
 * `z.infer` of a schema and its type stop agreeing in either direction, but only for the pairs the
 * tuple holds. This test fails on a cast whose schema and type the tuple does not hold to each
 * other, both ways (ADR-0023, decision 6).
 *
 * The TypeScript parser reads the file, because the types nest (`Required<Schemas['…']>`) and
 * prettier may break a line anywhere inside them. A cast of `z.array(X)` to `T[]` is a list of `X`,
 * and its pair is `X`'s with `T`. The test compares the text of the types only: whether a pair
 * holds is for the compiler to say.
 */

const FILE = 'src/api/responseSchemas.ts';

/** White space dropped but between two words: where prettier breaks a line changes nothing. */
const compact = (text: string) => text.replace(/\s*([^\w$\s])\s*/g, '$1').replace(/\s+/g, ' ');

interface Cast {
  /** Where the cast is, as `file:line`. */
  at: string;
  /** `z.infer<typeof X>` of the schema `X` that the cast narrows. */
  inferred: string;
  /** The `T` of `ZodType<T>`, or of `ZodType<T[]>` for a list. */
  type: string;
}

interface Read {
  casts: Cast[];
  /** The two arguments of each `AssertExtends` in the tuple. */
  pairs: [string, string][];
}

/** The `as ZodType<…>` casts in `code` and the pairs of its tuple, every type as compact text. */
function read(name: string, code: string): Read {
  const source = ts.createSourceFile(name, code, ts.ScriptTarget.Latest, false, ts.ScriptKind.TS);
  const textOf = (node: ts.Node) => compact(node.getText(source));
  /** The type arguments of a reference to `generic`, or undefined for any other node. */
  const argumentsOf = (node: ts.Node, generic: string) =>
    ts.isTypeReferenceNode(node) && textOf(node.typeName) === generic
      ? (node.typeArguments?.map(textOf) ?? [])
      : undefined;

  const casts: Cast[] = [];
  const pairs: [string, string][] = [];
  const visit = (node: ts.Node) => {
    if (ts.isAsExpression(node)) {
      const claimed = argumentsOf(node.type, 'ZodType');
      if (claimed?.length === 1) {
        const [type] = claimed;
        const expression = textOf(node.expression);
        const list = /^z\.array\((\w+)\)$/.exec(expression);
        const element = list !== null && type.endsWith('[]') ? list[1] : undefined;
        const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
        casts.push({
          at: `${name}:${line + 1}`,
          inferred: `z.infer<typeof ${element ?? expression}>`,
          type: element === undefined ? type : type.slice(0, -2),
        });
      }
    }
    if (
      ts.isTypeAliasDeclaration(node) &&
      node.name.text === '_GeneratedSchemasMatchSpec' &&
      ts.isTupleTypeNode(node.type)
    ) {
      for (const entry of node.type.elements) {
        const pair = argumentsOf(entry, 'AssertExtends');
        if (pair?.length === 2) pairs.push([pair[0], pair[1]]);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return { casts, pairs };
}

/** Each entry a cast needs and the tuple lacks, as `file:line AssertExtends<A, B>`. */
function missingPairs({ casts, pairs }: Read): string[] {
  return casts.flatMap(({ at, inferred, type }) =>
    [
      [inferred, type],
      [type, inferred],
    ]
      .filter(([first, second]) => !pairs.some(([a, b]) => a === first && b === second))
      .map(([first, second]) => `${at} AssertExtends<${first}, ${second}>`),
  );
}

const REAL = read(FILE, readFileSync(FILE, 'utf8'));

describe('the casts in responseSchemas.ts', () => {
  it('each have their schema and their type held to each other in the tuple, both ways', () => {
    expect(
      missingPairs(REAL),
      'A cast to ZodType<T> is the compiler taking our word that the schema parses a T. Add the ' +
        'missing entries to _GeneratedSchemasMatchSpec, written as the pairs already there are. ' +
        'If tsc then refuses one, the two generators disagree: fix the source, never the pair.',
    ).toEqual([]);
  });

  it('are found, and so is the tuple', () => {
    // CONTROL: a reader that found no cast would pass the test above.
    expect(REAL.casts.length).toBeGreaterThan(0);
    expect(REAL.pairs.length).toBeGreaterThan(0);
  });

  it('are each matched with their own schema, through a line break and a list', () => {
    const code = [
      'type _GeneratedSchemasMatchSpec = [',
      "  AssertExtends<z.infer<typeof A>, Schemas['A']>,",
      '  AssertExtends<',
      "    Schemas['A'],",
      '    z.infer<typeof A>',
      '  >,',
      "  AssertExtends<z.infer<typeof B>, Schemas['B']>,",
      '];',
      "const a = A as ZodType<Schemas['A']>;",
      "const list = z.array(A) as ZodType<Schemas['A'][]>;",
      'const b = B as ZodType<',
      "  Schemas['B']",
      '>;',
      "const c = C as ZodType<Schemas['A']>;",
      'const n = 1 as number;',
    ].join('\n');
    expect(missingPairs(read('planted.ts', code))).toEqual([
      "planted.ts:11 AssertExtends<Schemas['B'], z.infer<typeof B>>",
      "planted.ts:14 AssertExtends<z.infer<typeof C>, Schemas['A']>",
      "planted.ts:14 AssertExtends<Schemas['A'], z.infer<typeof C>>",
    ]);
  });
});
