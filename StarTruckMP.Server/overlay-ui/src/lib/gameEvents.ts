// ---------------------------------------------------------------------------
// WebView2 host object type declaration
// window.chrome.webview is injected by the WebView2 runtime and is only
// available when running inside the StarTruckMP.Overlay process.
// ---------------------------------------------------------------------------
declare global {
	interface Window {
		chrome?: {
			webview?: {
				postMessage(content: unknown): void;
			};
		};
		CefSharp?: {
			PostMessage?(content: unknown): void;
		};
		__starTruckMP?: {
			pendingGameMessages?: GameMessage[];
			handlers?: Array<(message: GameMessage) => void>;
			dispatchGameMessage?(message: GameMessage): void;
		};
	}
}

/**
 * Messages sent from the StarTruckMP game client to the overlay via
 * OverlayManager.PostMessage() are delivered as window.postMessage events
 * with this shape.
 */
export interface GameMessage<T = unknown> {
	/** Always "StarTruckMP" — use this to filter out unrelated messages. */
	source: 'StarTruckMP';
	/** Arbitrary event type string defined by the caller (e.g. "playerSpeed"). */
	type: string;
	/** Optional JSON payload, deserialized from the value passed in C#. */
	payload?: T;
}

type Handler<T> = (message: GameMessage<T>) => void;

/**
 * Subscribes to **all** game messages.
 *
 * @returns An unsubscribe function — call it to remove the listener.
 *
 * @example
 * const off = onGameMessage((msg) => console.log(msg.type, msg.payload));
 * // later:
 * off();
 */
export function onGameMessage<T = unknown>(handler: Handler<T>): () => void;

/**
 * Subscribes to game messages of a specific `type`.
 *
 * @returns An unsubscribe function — call it to remove the listener.
 *
 * @example
 * const off = onGameMessage('playerSpeed', (msg) => console.log(msg.payload));
 */
export function onGameMessage<T = unknown>(type: string, handler: Handler<T>): () => void;

export function onGameMessage<T = unknown>(
	typeOrHandler: string | Handler<T>,
	handler?: Handler<T>
): () => void {
	const filterType = typeof typeOrHandler === 'string' ? typeOrHandler : null;
	const cb = (typeof typeOrHandler === 'function' ? typeOrHandler : handler) as Handler<T>;
	const bridge = (window.__starTruckMP = window.__starTruckMP ?? {});
	bridge.pendingGameMessages = bridge.pendingGameMessages ?? [];
	bridge.handlers = bridge.handlers ?? [];

	const deliver = (data: GameMessage<T>) => {
		if (!data || data.source !== 'StarTruckMP') return;
		if (filterType !== null && data.type !== filterType) return;
		try {
			cb(data);
		} catch (err) {
			console.error('[StarTruckMP] Error in game message handler:', err);
		}
	};

	const listener = (event: MessageEvent) => {
		deliver(event.data as GameMessage<T>);
	};

	const bridgeListener = (event: Event) => {
		deliver((event as CustomEvent<GameMessage<T>>).detail);
	};

	const directBridgeHandler = (message: GameMessage) => {
		deliver(message as GameMessage<T>);
	};

	bridge.handlers.push(directBridgeHandler);
	bridge.dispatchGameMessage = (message: GameMessage) => {
		for (const registeredHandler of bridge.handlers ?? []) {
			registeredHandler(message);
		}
	};

	window.addEventListener('message', listener);
	window.addEventListener('StarTruckMP.GameMessage', bridgeListener as EventListener);

	const pendingMessages = [...(bridge.pendingGameMessages ?? [])];
	bridge.pendingGameMessages.length = 0;

	for (const pending of pendingMessages) {
		deliver(pending as GameMessage<T>);
	}

	return () => {
		bridge.handlers = (bridge.handlers ?? []).filter((registeredHandler) => registeredHandler !== directBridgeHandler);
		if ((bridge.handlers?.length ?? 0) === 0) {
			delete bridge.dispatchGameMessage;
		}

		window.removeEventListener('message', listener);
		window.removeEventListener('StarTruckMP.GameMessage', bridgeListener as EventListener);
	};
}

// ---------------------------------------------------------------------------
// Game → Overlay direction (JavaScript calling into C#)
// ---------------------------------------------------------------------------

/**
 * Sends a typed event from the Svelte overlay to the game plugin.
 * The game plugin receives it via `OverlayManager.MessageReceived`.
 *
 * Only works when running inside the WebView2 host process.
 * Calls are silently ignored in a regular browser (safe for development).
 *
 * @example
 * sendToGame('playerReady');
 * sendToGame('chatMessage', { text: 'Hello!' });
 */
export function sendToGame<T = unknown>(type: string, payload?: T): void {
	if (window.chrome?.webview) {
		window.chrome.webview.postMessage({ type, payload });
		return;
	}

	if (window.CefSharp?.PostMessage) {
		window.CefSharp.PostMessage({ type, payload });
		return;
	}

	console.warn('[StarTruckMP] sendToGame: no bridge available (WebView2/CefSharp).');
}

