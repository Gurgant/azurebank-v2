import { describe, expect, it } from 'vitest';
import { jsonMembersAsWritten } from './jsonMembers';

describe('jsonMembersAsWritten', () => {
  it('lists a member as many times as the text writes it, in the order written', () => {
    expect(
      jsonMembersAsWritten('{"name":"One","type":"Savings","Name":"Two","name":null}'),
    ).toEqual([
      ['name', 'One'],
      ['type', 'Savings'],
      ['Name', 'Two'],
      ['name', null],
    ]);
  });

  it('reads a name written with escapes as the name it spells', () => {
    expect(jsonMembersAsWritten('{"na\\u006de":"One","a\\"b":1,"c\\\\":2}')).toEqual([
      ['name', 'One'],
      ['a"b', 1],
      ['c\\', 2],
    ]);
  });

  it('lists only the members of the object itself, and folds nothing but what is inside a value', () => {
    expect(
      jsonMembersAsWritten('{"extra":{"name":1,"name":2},"list":[{"name":3},{"name":4}],"name":5}'),
    ).toEqual([
      ['extra', { name: 2 }],
      ['list', [{ name: 3 }, { name: 4 }]],
      ['name', 5],
    ]);
  });

  it.each([
    ['an empty object', '{}'],
    ['an empty object with white space', ' \r\n{ \t} \n'],
    ['every kind of value', '{"a":"x","b":-1.5e3,"c":true,"d":false,"e":null,"f":{},"g":[]}'],
    [
      'white space everywhere',
      ' {\n  "a" :\t1 ,\r\n  "b" : [ 1 , 2 ] ,\n  "c" : { "d" : "e" }\n} ',
    ],
    [
      'brackets, commas, colons and quotes inside strings',
      '{"a":"}","b":"{[,:","c":"say \\"}\\" twice","d":["]","}"],"e":{"f":"]}"},"g:h":"i,j"}',
    ],
    ['a string that ends with a backslash', '{"a":"\\\\","b":"\\\\\\"","c":1}'],
    ['a value that ends the object with no space', '{"a":1,"b":null}'],
    ['nesting several levels deep', '{"a":{"b":[{"c":[[1,{"d":"}"}]]}]},"e":2}'],
  ])('agrees with JSON.parse on %s', (_case, raw) => {
    // No member is written twice in these, so the two readings have to be one.
    expect(jsonMembersAsWritten(raw)).toEqual(
      Object.entries(JSON.parse(raw) as Record<string, unknown>),
    );
  });
});
