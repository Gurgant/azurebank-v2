import type { Locator } from '@playwright/test';

/**
 * How well a text field's words stand out from the field, as the browser drew them.
 *
 * Two ratios by the WCAG formula, from computed colours: what is typed against the field's own
 * ground, and the placeholder against the same ground. The colours come back with them, so that a
 * red comparison says which pair it measured and not only that the ratio was low.
 *
 * Computed, because that is where a field the stylesheet gave no background shows up: the browser
 * paints it its own default, which is white whatever the app's theme, and the computed style says
 * so. `wentThrough.spec.ts` measures a sentence the same way; this one is for a field, which has
 * a ground of its own and a second text, the placeholder.
 *
 * The ground is the field's background over whatever is behind it, down to the first opaque one;
 * the layers are composited by painting them on one pixel. It throws where it could not measure
 * instead of guessing: a value that is no colour, a background image on the way, nothing opaque
 * behind the field. What it does not see: an `opacity` on the field or around it, and what a
 * visitor's own forced colours would do.
 */
export function fieldContrast(field: Locator) {
  return field.evaluate((element) => {
    /** The colours, painted one over the other on one pixel: its red, green, blue and alpha. */
    const pixel = (layers: string[]) => {
      const canvas = document.createElement('canvas');
      canvas.width = 1;
      canvas.height = 1;
      const context = canvas.getContext('2d');
      if (context === null) throw new Error('no canvas to paint a colour on');
      for (const colour of layers) {
        if (!CSS.supports('color', colour)) throw new Error(`not a colour: ${colour}`);
        context.fillStyle = colour;
        context.fillRect(0, 0, 1, 1);
      }
      return [...context.getImageData(0, 0, 1, 1).data];
    };
    /** The same pixel as red, green and blue, which it is only when nothing shows through. */
    const paint = (layers: string[]) => {
      const [red, green, blue, alpha] = pixel(layers);
      if (alpha !== 255) throw new Error('nothing opaque behind the field');
      return [red, green, blue];
    };

    const luminance = (rgb: number[]) => {
      const [red, green, blue] = rgb.map((channel) => {
        const share = channel / 255;
        return share <= 0.04045 ? share / 12.92 : ((share + 0.055) / 1.055) ** 2.4;
      });
      return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    };
    const ratio = (one: number[], other: number[]) => {
      const [lighter, darker] = [luminance(one), luminance(other)].sort((a, b) => b - a);
      return Math.round(((lighter + 0.05) / (darker + 0.05)) * 100) / 100;
    };
    const hex = (rgb: number[]) =>
      `#${rgb.map((channel) => channel.toString(16).padStart(2, '0')).join('')}`;

    // From the field outwards, until a background that lets nothing through.
    const grounds: string[] = [];
    for (let at: Element | null = element; at !== null; at = at.parentElement) {
      const style = getComputedStyle(at);
      if (style.backgroundImage !== 'none') throw new Error('a background image is in the way');
      grounds.unshift(style.backgroundColor);
      if (pixel([style.backgroundColor])[3] === 255) break;
    }
    const ground = paint(grounds);
    const text = paint([...grounds, getComputedStyle(element).color]);
    const hint = paint([...grounds, getComputedStyle(element, '::placeholder').color]);

    return {
      typed: ratio(text, ground),
      placeholder: ratio(hint, ground),
      colours: { text: hex(text), placeholder: hex(hint), ground: hex(ground) },
    };
  });
}
