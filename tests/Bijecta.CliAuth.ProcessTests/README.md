# ProcessTests

Cross-process session race tests. Each `Bijecta.CliAuth.SessionProcess` child is a
separate CLI run: a real `CliAuthenticator` over a real `FileTokenStore` sharing one
temporary directory, with a fake protocol issuing synthetic credentials (ADRs 0006–0009).

The parent controls each child line by line (`login`, `login-pause`, `login-stage`,
`login-expiring`, `status`, `token`, `token-pause`, `logout`) and can hold a login inside
the protocol, where no lease is held, a login inside the commit just before publication,
where the commit lease is held and the staging file is written, or a refresh inside the
protocol, where the session lease is held. `login-expiring` issues a token already inside
the expiry margin. The tests cover:

- visibility of sessions written or removed by another process;
- concurrent logins from the same state committing exactly one record;
- a login superseded when another process replaced the record meanwhile, and later
  logins building on the latest generation (no lost update);
- logout superseding a login started while signed in, and the accepted ADR 0007
  trade-off for a login started while signed out;
- a process killed before publication: the old record stays readable, the lease is
  released by the operating system, and the next write removes the orphaned staging file;
- two processes sharing an expiring session making one refresh, the waiting process
  reloading the committed result;
- a process killed mid-refresh leaving the session and its refresh token untouched, so
  the next run refreshes (no in-progress marker, no forced login);
- logout and login in another process waiting for a running refresh and still winning:
  a refresh cannot resurrect a logged-out session or overwrite a newer login.

The helper is built by the solution and copied to the test output's `SessionProcess`
directory; the normal solution test command and the Ubuntu/Windows/macOS CI matrix run
this suite.
