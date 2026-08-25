package dev.kyletaylor.infraadvisor.mobile;

import com.datadog.android.Datadog;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;

public final class Session {
    private LoginResponse login;
    public LoginResponse getLogin() { return login; }
    public void setLogin(LoginResponse login) {
        // Only stable identity fields are sent. The JWT and password remain in
        // process memory and are never attached to RUM events or spans.
        this.login = login;
        Datadog.setUserInfo(login.user.id, null, login.user.email, java.util.Collections.emptyMap());
    }
    public void clear() {
        login = null;
        Datadog.clearUserInfo();
    }
}
