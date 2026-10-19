# One nomination per component

The controlling checklist previously created a nomination transaction for each
validated pair while earlier nomination responses were still pending. With
several reachable remote candidates, one endpoint could commit to a different
route from the other. Secure data arriving from the other route was correctly
rejected by ICE, leaving DTLS waiting for ServerHello until timeout.

The checklist now retains its nomination choice until acknowledgement, role
change or terminal failure. Connectivity checks on other candidates can continue,
but cannot nominate another route. Exhausting nomination retransmissions fails
this generation rather than guessing that the peer did not accept it. A role
change clears the old-role transaction and nomination choice together.

This follows [RFC 8445 section 8.1.1](https://www.rfc-editor.org/rfc/rfc8445.html#section-8.1.1).
No source-address admission, fingerprint, integrity, replay or consent check is
relaxed. No external implementation or RFC code component was imported.

Two loopback candidates of one synthetic authenticated ICE-lite peer expose the
race: nomination acknowledgements are delayed beyond the checklist interval,
with a separate first-acknowledgement-loss case. Only one distinct candidate may
receive a nomination, the selected endpoints must agree, retransmissions must
be exercised, and unselected-source application data must remain rejected.

The shared M4 admission guard rejects this repository before compilation.
Publication is gated by the existing protocol, integration, NativeAOT and
independent-peer CI matrix. Real-provider and physical-client acceptance remain
separate from those synthetic checks.
