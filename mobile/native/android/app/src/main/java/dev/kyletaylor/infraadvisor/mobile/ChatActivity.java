package dev.kyletaylor.infraadvisor.mobile;

import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.Toolbar;
import dev.kyletaylor.infraadvisor.mobile.api.ApiError;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationDetail;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationMessage;
import dev.kyletaylor.infraadvisor.mobile.model.ConversationSummary;
import dev.kyletaylor.infraadvisor.mobile.model.LoginResponse;
import dev.kyletaylor.infraadvisor.mobile.model.QueryResponse;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.UUID;

public final class ChatActivity extends AppCompatActivity {
    private static final List<String> SAMPLE_PROMPTS = Arrays.asList(
            "Choose a sample prompt…",
            "What infrastructure risks should a Texas city review before hurricane season?",
            "Summarize FEMA flood disaster declarations in Texas since 2015 by county.",
            "What should a city evaluate before replacing an aging bridge?",
            "What current federal procurement opportunities exist related to operational resilience or emergency management enhancements in Texas infrastructure systems?"
    );

    private Button ask;
    private Button newChat;
    private ProgressBar progress;
    private TextView error;
    private TextView trace;
    private TextView emptyHistory;
    private EditText prompt;
    private Spinner conversationPicker;
    private Spinner backend;
    private Spinner model;
    private Spinner samples;
    private LinearLayout history;
    private ScrollView historyScroll;
    private LoginResponse login;
    private List<ConversationSummary> conversations = new ArrayList<>();
    private String sessionId = UUID.randomUUID().toString();
    private String conversationId;
    private boolean hasMessages;
    private boolean suppressConversationSelection;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        login = app().session().getLogin();
        if (login == null) { openLogin(); return; }
        setContentView(R.layout.activity_chat);
        SystemBarInsets.apply(findViewById(R.id.screen_root));
        setSupportActionBar((Toolbar) findViewById(R.id.toolbar));
        setTitle(R.string.chat_title);
        bindViews();
        AppTabs.bind(this, AppTabs.Destination.CHAT);
        configureSelectors();
        ask.setOnClickListener(view -> submit());
        newChat.setOnClickListener(view -> resetChat());
        loadConversations(null);
    }

    @Override protected void onResume() {
        super.onResume();
        if (app().session().getLogin() == null) openLogin();
    }

    private void bindViews() {
        prompt = findViewById(R.id.prompt);
        ask = findViewById(R.id.ask);
        newChat = findViewById(R.id.new_chat);
        progress = findViewById(R.id.progress);
        error = findViewById(R.id.error);
        trace = findViewById(R.id.trace);
        conversationPicker = findViewById(R.id.conversations);
        backend = findViewById(R.id.backend);
        model = findViewById(R.id.model);
        samples = findViewById(R.id.sample_prompts);
        history = findViewById(R.id.chat_history);
        historyScroll = findViewById(R.id.history_scroll);
        emptyHistory = findViewById(R.id.empty_history);
    }

    private void configureSelectors() {
        conversationPicker.setAdapter(adapter(Collections.singletonList(getString(R.string.new_conversation))));
        conversationPicker.setOnItemSelectedListener(new SimpleSelectionListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (suppressConversationSelection) return;
                if (position == 0) resetChat();
                else loadConversation(conversations.get(position - 1));
            }
        });
        backend.setAdapter(adapter(Arrays.asList("Python", ".NET")));
        samples.setAdapter(adapter(SAMPLE_PROMPTS));
        samples.setOnItemSelectedListener(new SimpleSelectionListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (position > 0) {
                    prompt.setText(SAMPLE_PROMPTS.get(position));
                    prompt.setSelection(prompt.length());
                    samples.setSelection(0);
                }
            }
        });
        backend.setOnItemSelectedListener(new SimpleSelectionListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (conversationId == null) loadModels(null);
            }
        });
    }

    private ArrayAdapter<String> adapter(List<String> values) {
        ArrayAdapter<String> valueAdapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, values);
        valueAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        return valueAdapter;
    }

    private void loadConversations(String selectId) {
        app().api().listConversations(login.token, new ApiClient.Result<List<ConversationSummary>>() {
            @Override public void success(List<ConversationSummary> values) {
                conversations = values;
                List<String> labels = new ArrayList<>();
                labels.add(getString(R.string.new_conversation));
                for (ConversationSummary value : values) {
                    labels.add(value.title + " · " + (ApiClient.BACKEND_DOTNET.equals(value.backend) ? ".NET" : "Python"));
                }
                suppressConversationSelection = true;
                conversationPicker.setAdapter(adapter(labels));
                int position = 0;
                if (selectId != null) {
                    for (int index = 0; index < values.size(); index++) {
                        if (selectId.equals(values.get(index).id)) { position = index + 1; break; }
                    }
                }
                conversationPicker.setSelection(position, false);
                conversationPicker.post(() -> suppressConversationSelection = false);
            }
            @Override public void failure(Exception failure) { error.setText(readable(failure)); }
        });
    }

    private void loadConversation(ConversationSummary summary) {
        setLoading(true);
        app().api().conversation(login.token, summary.id, new ApiClient.Result<ConversationDetail>() {
            @Override public void success(ConversationDetail detail) {
                conversationId = detail.id;
                sessionId = detail.id;
                backend.setSelection(ApiClient.BACKEND_DOTNET.equals(detail.backend) ? 1 : 0, false);
                backend.setEnabled(false);
                renderHistory(detail.messages);
                loadModels(detail.model);
                setLoading(false);
            }
            @Override public void failure(Exception failure) { setLoading(false); error.setText(readable(failure)); }
        });
    }

    private void loadModels(String desiredModel) {
        model.setEnabled(false);
        app().api().models(selectedBackend(), new ApiClient.Result<ApiClient.ModelOptions>() {
            @Override public void success(ApiClient.ModelOptions value) {
                model.setAdapter(adapter(value.models));
                String target = desiredModel == null ? value.defaultModel : desiredModel;
                model.setSelection(Math.max(0, value.models.indexOf(target)));
                model.setEnabled(true);
            }
            @Override public void failure(Exception failure) {
                model.setAdapter(adapter(Collections.singletonList("gpt-4.1-mini")));
                model.setEnabled(true);
                error.setText("Could not load models; using gpt-4.1-mini. " + readable(failure));
            }
        });
    }

    private void submit() {
        String question = prompt.getText().toString().trim();
        if (question.isEmpty() || model.getSelectedItem() == null) return;
        if (conversationId == null) {
            setLoading(true);
            String title = question.substring(0, Math.min(72, question.length()));
            app().api().createConversation(login.token, title, model.getSelectedItem().toString(), selectedBackend(),
                    new ApiClient.Result<ConversationSummary>() {
                        @Override public void success(ConversationSummary created) {
                            conversationId = created.id;
                            sessionId = created.id;
                            backend.setEnabled(false);
                            sendQuery(question);
                        }
                        @Override public void failure(Exception failure) { setLoading(false); error.setText(readable(failure)); }
                    });
        } else {
            setLoading(true);
            sendQuery(question);
        }
    }

    private void sendQuery(String question) {
        String selectedModel = model.getSelectedItem().toString();
        addMessage("You", question, false);
        prompt.setText("");
        app().api().query(login.token, question, sessionId, selectedModel, selectedBackend(), login.user.id, conversationId,
                new ApiClient.Result<QueryResponse>() {
                    @Override public void success(QueryResponse value) {
                        setLoading(false);
                        addMessage("Infra Advisor", value.answer, true);
                        renderMetadata(value);
                        if (!value.sources.isEmpty()) addMessage("Sources", "• " + String.join("\n• ", value.sources), true);
                        loadConversations(conversationId);
                    }
                    @Override public void failure(Exception failure) {
                        setLoading(false);
                        error.setText(readable(failure));
                        prompt.setText(question);
                        prompt.setSelection(prompt.length());
                    }
                });
    }

    private void renderHistory(List<ConversationMessage> values) {
        clearHistory();
        for (ConversationMessage value : values) {
            addMessage("user".equals(value.role) ? "You" : "Infra Advisor", value.content, !"user".equals(value.role));
            if (!value.sources.isEmpty()) addMessage("Sources", "• " + String.join("\n• ", value.sources), true);
        }
    }

    private void addMessage(String role, String content, boolean assistant) {
        if (!hasMessages) {
            emptyHistory.setVisibility(View.GONE);
            hasMessages = true;
        }
        TextView message = new TextView(this);
        message.setText(role + "\n" + content);
        message.setTextColor(Color.rgb(31, 31, 31));
        message.setTextSize(16f);
        message.setPadding(dp(14), dp(12), dp(14), dp(12));
        message.setBackgroundColor(assistant ? Color.rgb(247, 242, 251) : Color.rgb(232, 242, 252));
        message.setTypeface(Typeface.DEFAULT, Typeface.NORMAL);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        params.bottomMargin = dp(10);
        history.addView(message, params);
        historyScroll.post(() -> historyScroll.fullScroll(View.FOCUS_DOWN));
    }

    private void renderMetadata(QueryResponse value) {
        trace.setText("Trace " + (value.traceId == null ? "unavailable" : value.traceId)
                + " · Session " + value.sessionId + " · " + value.model);
    }

    private void resetChat() {
        conversationId = null;
        sessionId = UUID.randomUUID().toString();
        clearHistory();
        backend.setEnabled(true);
        error.setText("");
        trace.setText("");
        prompt.setText("");
        suppressConversationSelection = true;
        conversationPicker.setSelection(0, false);
        conversationPicker.post(() -> suppressConversationSelection = false);
        loadModels(null);
    }

    private void clearHistory() {
        hasMessages = false;
        history.removeAllViews();
        history.addView(emptyHistory);
        emptyHistory.setVisibility(View.VISIBLE);
    }

    private String selectedBackend() {
        return backend.getSelectedItemPosition() == 1 ? ApiClient.BACKEND_DOTNET : ApiClient.BACKEND_PYTHON;
    }

    private String readable(Exception failure) {
        return failure instanceof com.android.volley.VolleyError
                ? ApiError.message((com.android.volley.VolleyError) failure)
                : (failure.getMessage() == null ? "Request failed" : failure.getMessage());
    }

    @Override public boolean onCreateOptionsMenu(Menu menu) {
        menu.add(R.string.logout).setShowAsAction(MenuItem.SHOW_AS_ACTION_NEVER);
        return true;
    }

    @Override public boolean onOptionsItemSelected(MenuItem item) {
        if (item.getTitle().equals(getString(R.string.logout))) { app().session().clear(); openLogin(); return true; }
        return super.onOptionsItemSelected(item);
    }

    private void setLoading(boolean loading) {
        ask.setEnabled(!loading);
        newChat.setEnabled(!loading);
        conversationPicker.setEnabled(!loading);
        model.setEnabled(!loading);
        progress.setVisibility(loading ? View.VISIBLE : View.GONE);
        if (loading) error.setText("");
    }

    private void openLogin() {
        Intent intent = new Intent(this, LoginActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
        startActivity(intent);
        finish();
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private InfraAdvisorApplication app() { return (InfraAdvisorApplication) getApplication(); }

    private abstract static class SimpleSelectionListener implements AdapterView.OnItemSelectedListener {
        @Override public void onNothingSelected(AdapterView<?> parent) {}
    }
}
