// How the server writes a name: System.Text.Json's camel-case policy, which is what every row's keys,
// every wire property and every command payload are spelled with. Ported a UTF-16 unit at a time,
// because that is how the policy reads a name: a letter outside the basic plane is two halves, neither
// of them a capital, and is left as it is.

const capital = /^\p{Lu}$/u;

function isCapital(unit: string): boolean {
  return capital.test(unit);
}

// Lowered on its own, as .NET lowers a char: a capital whose lowercase is more than one unit (only
// 'İ', whose lowering adds a combining dot) has no single char to become, so it stays.
function lower(unit: string): string {
  const lowered = unit.toLowerCase();
  if (lowered.length === 1) {
    return lowered;
  }

  return unit;
}

/**
 * A name as the server writes it. The run of capitals a name starts with is lowered, up to the one
 * that starts the next word: `URLValue` becomes `urlValue`, `ID` becomes `id`.
 */
export function camelCase(name: string): string {
  if (name.length === 0 ||
      !isCapital(name.charAt(0))) {
    return name;
  }

  const units = name.split("");
  for (let index = 0; index < units.length; index++) {
    if (index === 1 &&
        !isCapital(units[1]!)) {
      break;
    }

    const next = units[index + 1];
    // Stop at the capital before a letter that is not one: it starts the next word.
    if (index > 0 &&
        next !== undefined &&
        !isCapital(next)) {
      // Unless what follows is a space, which ends the run with this one in it.
      if (next === " ") {
        units[index] = lower(units[index]!);
      }

      break;
    }

    units[index] = lower(units[index]!);
  }

  return units.join("");
}
