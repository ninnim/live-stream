import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import LoginPage from "@/app/login/page";

const { routerMock, ssoMock, authMock, tokenMock } = vi.hoisted(() => ({
  routerMock: { push: vi.fn(), replace: vi.fn() },
  ssoMock: { discover: vi.fn(), start: vi.fn(), complete: vi.fn() },
  authMock: { login: vi.fn() },
  tokenMock: { save: vi.fn() },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => routerMock,
  useSearchParams: () => new URLSearchParams(),
}));

vi.mock("@/lib/api/sso", () => ({ ssoApi: ssoMock }));
vi.mock("@/lib/api/live-sessions", () => ({ authApi: authMock }));

vi.mock("@/lib/api/client", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/client")>("@/lib/api/client");
  return { ...actual, tokenStore: tokenMock };
});

beforeEach(() => {
  vi.clearAllMocks();
  ssoMock.discover.mockResolvedValue({ available: false, workspaceName: null });
});

afterEach(() => {
  vi.useRealTimers();
});

describe("Sign-in with single sign-on", () => {
  it("offers single sign-on once the address is known to use it", async () => {
    ssoMock.discover.mockResolvedValue({ available: true, workspaceName: "Northwind" });

    render(<LoginPage />);
    await userEvent.type(screen.getByLabelText("Email"), "person@northwind.example");

    expect(await screen.findByText("Northwind uses single sign-on.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Continue with single sign-on/ })).toBeEnabled();
  });

  it("does not ask about an address that is not finished yet", async () => {
    render(<LoginPage />);
    await userEvent.type(screen.getByLabelText("Email"), "person");

    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(ssoMock.discover).not.toHaveBeenCalled();
  });

  it("keeps the password form usable when discovery fails", async () => {
    // An identity provider being unreachable must not take password sign-in with it.
    ssoMock.discover.mockRejectedValue(new Error("network"));

    render(<LoginPage />);
    await userEvent.type(screen.getByLabelText("Email"), "person@example.com");

    await waitFor(() => expect(ssoMock.discover).toHaveBeenCalled());
    expect(screen.queryByRole("button", { name: /Continue with single sign-on/ })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sign in" })).toBeEnabled();
  });

  it("sends the browser to the provider when single sign-on is chosen", async () => {
    ssoMock.discover.mockResolvedValue({ available: true, workspaceName: "Northwind" });
    ssoMock.start.mockResolvedValue({
      authorizationUrl: "https://login.example.com/authorize?state=abc",
      state: "abc",
    });

    const assign = vi.fn();
    Object.defineProperty(window, "location", {
      configurable: true,
      value: { ...window.location, assign },
    });

    render(<LoginPage />);
    await userEvent.type(screen.getByLabelText("Email"), "person@northwind.example");
    await userEvent.click(await screen.findByRole("button", { name: /Continue with single sign-on/ }));

    await waitFor(() => expect(assign).toHaveBeenCalledWith("https://login.example.com/authorize?state=abc"));
    expect(ssoMock.start).toHaveBeenCalledWith("person@northwind.example");
  });

  it("reports a provider that could not be reached instead of hanging", async () => {
    ssoMock.discover.mockResolvedValue({ available: true, workspaceName: null });
    ssoMock.start.mockRejectedValue(new Error("unreachable"));

    render(<LoginPage />);
    await userEvent.type(screen.getByLabelText("Email"), "person@northwind.example");
    await userEvent.click(await screen.findByRole("button", { name: /Continue with single sign-on/ }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/could not reach/i);
  });

  it("still signs in with a password when the workspace also has single sign-on", async () => {
    ssoMock.discover.mockResolvedValue({ available: true, workspaceName: "Northwind" });
    authMock.login.mockResolvedValue({ accessToken: "token", user: { id: "u" } });

    render(<LoginPage />);
    await userEvent.type(screen.getByLabelText("Email"), "person@northwind.example");
    await userEvent.type(screen.getByLabelText("Password"), "a-long-enough-password");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(tokenMock.save).toHaveBeenCalled());
    expect(routerMock.push).toHaveBeenCalledWith("/dashboard");
  });
});
