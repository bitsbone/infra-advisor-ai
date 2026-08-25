package dev.kyletaylor.infraadvisor.mobile;

import android.content.Intent;
import android.os.Bundle;
import android.view.Menu;
import android.view.MenuItem;
import android.widget.TextView;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.Toolbar;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;

public final class InfoActivity extends AppCompatActivity {
    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        LoginResponse login = app().session().getLogin();
        if (login == null) { openLogin(); return; }
        setContentView(R.layout.activity_info);
        SystemBarInsets.apply(findViewById(R.id.screen_root));
        setSupportActionBar((Toolbar) findViewById(R.id.toolbar));
        setTitle(R.string.info);
        AppTabs.bind(this, AppTabs.Destination.INFO);
        ((TextView) findViewById(R.id.profile_details)).setText(
                "Email\n" + login.user.email + "\n\nUser ID\n" + login.user.id + "\n\nAdmin\n" + (login.user.isAdmin ? "Yes" : "No"));
        ((TextView) findViewById(R.id.datadog_details)).setText(
                "Site\n" + BuildConfig.DD_SITE + "\n\nEnvironment\n" + BuildConfig.DD_ENV
                        + "\n\nService\n" + BuildConfig.DD_SERVICE + "\n\nRUM application\n" + BuildConfig.DD_RUM_APPLICATION_ID
                        + "\n\nRUM sampling\n100%\n\nReplay sampling\n100%\n\nReplay privacy\nMask sensitive inputs"
                        + "\n\nTrace sampling\n" + Math.round(BuildConfig.DD_TRACE_SAMPLE_RATE) + "%"
                        + "\n\nLogs\nEnabled · 100% sampling"
                        + "\n\nCrash reporting\nEnabled"
                        + "\n\nCrash symbols\nRelease R8 mapping upload");
        ((TextView) findViewById(R.id.api_details)).setText("Base URL\n" + BuildConfig.API_BASE_URL);
    }

    @Override public boolean onCreateOptionsMenu(Menu menu) {
        menu.add(R.string.logout).setShowAsAction(MenuItem.SHOW_AS_ACTION_NEVER);
        return true;
    }

    @Override public boolean onOptionsItemSelected(MenuItem item) {
        if (item.getTitle().equals(getString(R.string.logout))) { app().session().clear(); openLogin(); return true; }
        return super.onOptionsItemSelected(item);
    }

    private void openLogin() {
        Intent intent = new Intent(this, LoginActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
        startActivity(intent);
        finish();
    }

    private InfraAdvisorApplication app() { return (InfraAdvisorApplication) getApplication(); }
}
