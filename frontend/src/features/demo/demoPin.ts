/*
  The PIN a demo copy starts with, as the page prints it.

  The server seeds every user of a copy with these digits and says they are no secret: what keeps
  a copy private is its owner's sign-in, never the PIN
  (backend/src/AzureBank.Shared/Constants/DemoCopyDefaults.cs). So the page may print them to
  anyone who is signed in to a copy.

  One constant, and not the `pin` that a claim's answer carries and the browser keeps
  (src/features/demo/demoCopyStorage.ts): a visitor who signed in to a copy on another browser has
  no kept copy to read it from, and two sources for one line could disagree.

  A file of its own, with no import: a component file exports components only, and whatever prints
  the digits takes them from here without pulling anything in behind them.
*/
export const DEMO_PIN = '123456';
