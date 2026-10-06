/*
  Is this page the demo?

  The page says so itself, with one tag: `<meta name="azurebank-demo" content="true">`. The demo
  is on when the page's `meta` of that name says exactly `true`. A page without the tag is off, and
  so is a tag that says anything else or says nothing: a tag that is there and says `false` must
  not turn the demo on, so its presence alone decides nothing.

  A function that reads the page each time it is asked, and never a constant worked out when this
  module loads: a test puts the tag on the page after the modules are in, and a constant would have
  answered before it.

  This file imports nothing, so anything may ask it without pulling the app in behind the question.
*/
export function isDemoMode(): boolean {
  return document.querySelector('meta[name="azurebank-demo"]')?.getAttribute('content') === 'true';
}
