import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { type StudioHotkeyActions, isTypingTarget, useStudioHotkeys } from "@/hooks/useStudioHotkeys";

function buildActions(): StudioHotkeyActions {
  return {
    recallScene: vi.fn(),
    cutToSource: vi.fn(),
    setLayout: vi.fn(),
    toggleLowerThird: vi.fn(),
    toggleMicrophone: vi.fn(),
    toggleCamera: vi.fn(),
  };
}

function Harness({ actions, enabled = true }: { actions: StudioHotkeyActions; enabled?: boolean }) {
  useStudioHotkeys(actions, enabled);

  return (
    <div>
      <input aria-label="Scene name" />
      <textarea aria-label="Notes" />
    </div>
  );
}

describe("useStudioHotkeys", () => {
  it("recalls a scene from a number key", () => {
    const actions = buildActions();
    render(<Harness actions={actions} />);

    fireEvent.keyDown(window, { key: "3", code: "Digit3" });

    expect(actions.recallScene).toHaveBeenCalledWith(2);
  });

  it("cuts to a source from shift and a number", () => {
    // Shift+number arrives as a symbol on most keyboard layouts, so the binding uses the physical
    // key. A test keyed on `key` alone would pass on a US layout and fail everywhere else.
    const actions = buildActions();
    render(<Harness actions={actions} />);

    fireEvent.keyDown(window, { key: "!", code: "Digit1", shiftKey: true });

    expect(actions.cutToSource).toHaveBeenCalledWith(0);
    expect(actions.recallScene).not.toHaveBeenCalled();
  });

  it("selects layouts", () => {
    const actions = buildActions();
    render(<Harness actions={actions} />);

    fireEvent.keyDown(window, { key: "q", code: "KeyQ" });
    fireEvent.keyDown(window, { key: "w", code: "KeyW" });
    fireEvent.keyDown(window, { key: "e", code: "KeyE" });

    expect(actions.setLayout).toHaveBeenNthCalledWith(1, "solo");
    expect(actions.setLayout).toHaveBeenNthCalledWith(2, "side-by-side");
    expect(actions.setLayout).toHaveBeenNthCalledWith(3, "picture-in-picture");
  });

  it("toggles the caption, the microphone and the camera", () => {
    const actions = buildActions();
    render(<Harness actions={actions} />);

    fireEvent.keyDown(window, { key: "l", code: "KeyL" });
    fireEvent.keyDown(window, { key: "m", code: "KeyM" });
    fireEvent.keyDown(window, { key: "v", code: "KeyV" });

    expect(actions.toggleLowerThird).toHaveBeenCalled();
    expect(actions.toggleMicrophone).toHaveBeenCalled();
    expect(actions.toggleCamera).toHaveBeenCalled();
  });

  it("accepts an uppercase key, because Caps Lock is not a mode change", () => {
    const actions = buildActions();
    render(<Harness actions={actions} />);

    fireEvent.keyDown(window, { key: "M", code: "KeyM" });

    expect(actions.toggleMicrophone).toHaveBeenCalled();
  });

  it("never steals a keystroke from someone typing", () => {
    // A producer naming a scene "1" must get the character, not a cut.
    const actions = buildActions();
    const { getByLabelText } = render(<Harness actions={actions} />);

    fireEvent.keyDown(getByLabelText("Scene name"), { key: "1", code: "Digit1" });
    fireEvent.keyDown(getByLabelText("Notes"), { key: "m", code: "KeyM" });

    expect(actions.recallScene).not.toHaveBeenCalled();
    expect(actions.toggleMicrophone).not.toHaveBeenCalled();
  });

  it("ignores keys held with a command or control modifier", () => {
    // Those belong to the browser and the operating system — Ctrl+1 switches tab.
    const actions = buildActions();
    render(<Harness actions={actions} />);

    fireEvent.keyDown(window, { key: "1", code: "Digit1", ctrlKey: true });
    fireEvent.keyDown(window, { key: "1", code: "Digit1", metaKey: true });

    expect(actions.recallScene).not.toHaveBeenCalled();
  });

  it("has no binding that could end a broadcast", () => {
    // Deliberate: ending a show with a stray keystroke is a failure no undo repairs.
    const actions = buildActions();
    render(<Harness actions={actions} />);

    for (const key of ["s", "x", "Escape", "Enter", " ", "Delete"]) {
      fireEvent.keyDown(window, { key, code: `Key${key.toUpperCase()}` });
    }

    expect(Object.values(actions).every((action) => (action as ReturnType<typeof vi.fn>).mock.calls.length === 0))
      .toBe(true);
  });

  it("does nothing while disabled", () => {
    const actions = buildActions();
    render(<Harness actions={actions} enabled={false} />);

    fireEvent.keyDown(window, { key: "1", code: "Digit1" });

    expect(actions.recallScene).not.toHaveBeenCalled();
  });

  it("stops listening once unmounted", () => {
    const actions = buildActions();
    const { unmount } = render(<Harness actions={actions} />);

    unmount();
    fireEvent.keyDown(window, { key: "1", code: "Digit1" });

    expect(actions.recallScene).not.toHaveBeenCalled();
  });
});

describe("isTypingTarget", () => {
  it("recognises the elements a person types into", () => {
    const editable = document.createElement("div");
    // jsdom does not implement `isContentEditable`, so setting `contentEditable` alone leaves it
    // undefined. Defining it directly is what actually exercises the branch.
    Object.defineProperty(editable, "isContentEditable", { value: true });

    expect(isTypingTarget(document.createElement("input"))).toBe(true);
    expect(isTypingTarget(document.createElement("textarea"))).toBe(true);
    expect(isTypingTarget(document.createElement("select"))).toBe(true);
    expect(isTypingTarget(editable)).toBe(true);
    expect(isTypingTarget(document.createElement("div"))).toBe(false);
    expect(isTypingTarget(null)).toBe(false);
  });
});
