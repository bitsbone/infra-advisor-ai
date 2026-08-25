package dev.kyletaylor.infraadvisor.mobile;

import android.os.Bundle;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ProgressBar;
import android.widget.TextView;
import androidx.appcompat.app.AppCompatActivity;
import dev.kyletaylor.infraadvisor.mobile.api.ApiError;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import java.util.UUID;

public final class ChatActivity extends AppCompatActivity {
    private Button ask;
    private ProgressBar progress;
    private TextView error;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        LoginResponse login = app().session().getLogin();
        if (login == null) { finish(); return; }
        setTitle(R.string.chat_title);
        setContentView(R.layout.activity_chat);
        ((TextView) findViewById(R.id.signed_in_user)).setText("Signed in as " + login.user.email);
        EditText prompt = findViewById(R.id.prompt);
        prompt.setText("What infrastructure risks should a Texas city review before hurricane season?");
        ask = findViewById(R.id.ask);
        progress = findViewById(R.id.progress);
        error = findViewById(R.id.error);
        ask.setOnClickListener(view -> {
            String value = prompt.getText().toString().trim();
            if (value.isEmpty()) return;
            setLoading(true);
            app().api().query(login.token, value, UUID.randomUUID().toString(), new ApiClient.Result<QueryResponse>() {
                @Override public void success(QueryResponse value) { setLoading(false); render(value); }
                @Override public void failure(Exception failure) {
                    setLoading(false);
                    error.setText(failure instanceof com.android.volley.VolleyError ? ApiError.message((com.android.volley.VolleyError) failure) : failure.getMessage());
                }
            });
        });
    }

    @Override public boolean onCreateOptionsMenu(Menu menu) {
        menu.add(R.string.logout).setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS);
        return true;
    }
    @Override public boolean onOptionsItemSelected(MenuItem item) {
        if (item.getTitle().equals(getString(R.string.logout))) { app().session().clear(); finish(); return true; }
        return super.onOptionsItemSelected(item);
    }
    private void render(QueryResponse value) {
        ((TextView) findViewById(R.id.answer)).setText(value.answer);
        ((TextView) findViewById(R.id.sources)).setText(value.sources.isEmpty() ? "" : "Sources\n• " + String.join("\n• ", value.sources));
        ((TextView) findViewById(R.id.trace)).setText("Backend trace: " + (value.traceId == null ? "Unavailable" : value.traceId) + "\nSession: " + value.sessionId + "\nModel: " + value.model);
    }
    private void setLoading(boolean loading) {
        ask.setEnabled(!loading);
        progress.setVisibility(loading ? View.VISIBLE : View.GONE);
        if (loading) error.setText("");
    }
    private InfraAdvisorApplication app() { return (InfraAdvisorApplication) getApplication(); }
}
