/*
 * SonarOmnisharp
 * Copyright (C) 2021-2021 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */
package org.sonarsource.sonarlint.omnisharp;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.function.Consumer;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;
import org.mockito.ArgumentCaptor;
import org.sonar.api.batch.fs.InputFile;
import org.sonar.api.batch.fs.internal.TestInputFileBuilder;
import org.sonar.api.batch.rule.internal.ActiveRulesBuilder;
import org.sonar.api.batch.rule.internal.NewActiveRule;
import org.sonar.api.batch.sensor.internal.DefaultSensorDescriptor;
import org.sonar.api.batch.sensor.internal.SensorContextTester;
import org.sonar.api.batch.sensor.issue.Issue;
import org.sonar.api.config.Configuration;
import org.sonar.api.rule.RuleKey;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.tuple;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.verifyNoInteractions;
import static org.mockito.Mockito.verifyNoMoreInteractions;
import static org.mockito.Mockito.when;

class OmnisharpSensorTests {

  private final OmnisharpServer mockServer = mock(OmnisharpServer.class);
  private final OmnisharpProtocol mockProtocol = mock(OmnisharpProtocol.class);
  private OmnisharpSensor underTest;
  private Path baseDir;

  @BeforeEach
  void prepare(@TempDir Path tmp) throws Exception {
    baseDir = tmp.toRealPath();
    underTest = new OmnisharpSensor(mockServer, mockProtocol);
  }

  @Test
  void describe() {
    DefaultSensorDescriptor descriptor = new DefaultSensorDescriptor();

    underTest.describe(descriptor);

    assertThat(descriptor.name()).isEqualTo("OmniSharp");
    assertThat(descriptor.languages()).containsOnly(CSharpPlugin.LANGUAGE_KEY);
    assertThat(descriptor.ruleRepositories()).containsOnly(CSharpPlugin.REPOSITORY_KEY);

    Configuration configWithProp = mock(Configuration.class);
    when(configWithProp.hasKey(CSharpPropertyDefinitions.getOmnisharpLocation())).thenReturn(true);

    Configuration configWithoutProp = mock(Configuration.class);

    assertThat(descriptor.configurationPredicate()).accepts(configWithProp).rejects(configWithoutProp);

  }

  @Test
  void noopIfNoFiles() throws Exception {
    SensorContextTester sensorContext = SensorContextTester.create(baseDir);

    underTest.execute(sensorContext);

    verifyNoInteractions(mockProtocol, mockServer);
  }

  @Test
  void scanCsFile() throws Exception {
    SensorContextTester sensorContext = SensorContextTester.create(baseDir);

    Path filePath = baseDir.resolve("Foo.cs");
    String content = "Console.WriteLine(\"Hello World!\");";
    Files.write(filePath, content.getBytes(StandardCharsets.UTF_8));

    InputFile file = TestInputFileBuilder.create("", "Foo.cs")
      .setModuleBaseDir(baseDir)
      .setLanguage(CSharpPlugin.LANGUAGE_KEY)
      .setCharset(StandardCharsets.UTF_8)
      .build();
    sensorContext.fileSystem().add(file);

    underTest.execute(sensorContext);

    verify(mockServer).lazyStart(baseDir, null);

    verify(mockProtocol).updateBuffer(filePath.toFile(), content);
    verify(mockProtocol).codeCheck(eq(filePath.toFile()), any());
  }

  @Test
  void testCancellation() throws Exception {
    SensorContextTester sensorContext = SensorContextTester.create(baseDir);
    sensorContext.setCancelled(true);

    Path filePath = baseDir.resolve("Foo.cs");
    String content = "Console.WriteLine(\"Hello World!\");";
    Files.write(filePath, content.getBytes(StandardCharsets.UTF_8));

    InputFile file = TestInputFileBuilder.create("", "Foo.cs")
      .setModuleBaseDir(baseDir)
      .setLanguage(CSharpPlugin.LANGUAGE_KEY)
      .setCharset(StandardCharsets.UTF_8)
      .build();
    sensorContext.fileSystem().add(file);

    underTest.execute(sensorContext);

    verify(mockServer).lazyStart(baseDir, null);
    verify(mockProtocol).config(any());
    verifyNoMoreInteractions(mockProtocol);
  }

  @Test
  void ignoreInactiveRules() throws Exception {
    SensorContextTester sensorContext = SensorContextTester.create(baseDir);

    Path filePath = baseDir.resolve("Foo.cs");
    String content = "Console.WriteLine(\"Hello World!\");";
    Files.write(filePath, content.getBytes(StandardCharsets.UTF_8));

    InputFile file = TestInputFileBuilder.create("", "Foo.cs")
      .setModuleBaseDir(baseDir)
      .setLanguage(CSharpPlugin.LANGUAGE_KEY)
      .setCharset(StandardCharsets.UTF_8)
      .build();
    sensorContext.fileSystem().add(file);

    ArgumentCaptor<Consumer<OmnisharpDiagnostic>> captor = ArgumentCaptor.forClass(Consumer.class);

    underTest.execute(sensorContext);

    verify(mockProtocol).codeCheck(eq(filePath.toFile()), captor.capture());

    Consumer<OmnisharpDiagnostic> issueConsummer = captor.getValue();

    OmnisharpDiagnostic diag = new OmnisharpDiagnostic();
    diag.id = "SA12345";

    issueConsummer.accept(diag);

    assertThat(sensorContext.allIssues()).isEmpty();
  }

  @Test
  void reportIssueForActiveRules() throws Exception {
    SensorContextTester sensorContext = SensorContextTester.create(baseDir);

    RuleKey ruleKey = RuleKey.of(CSharpPlugin.REPOSITORY_KEY, "S12345");
    sensorContext.setActiveRules(new ActiveRulesBuilder().addRule(new NewActiveRule.Builder().setRuleKey(ruleKey).build()).build());

    Path filePath = baseDir.resolve("Foo.cs");
    String content = "Console.WriteLine(\"Hello World!\");";
    Files.write(filePath, content.getBytes(StandardCharsets.UTF_8));

    InputFile file = TestInputFileBuilder.create("", "Foo.cs")
      .setModuleBaseDir(baseDir)
      .setLanguage(CSharpPlugin.LANGUAGE_KEY)
      .setCharset(StandardCharsets.UTF_8)
      .initMetadata(content)
      .build();
    sensorContext.fileSystem().add(file);

    ArgumentCaptor<Consumer<OmnisharpDiagnostic>> captor = ArgumentCaptor.forClass(Consumer.class);

    underTest.execute(sensorContext);

    verify(mockProtocol).codeCheck(eq(filePath.toFile()), captor.capture());

    Consumer<OmnisharpDiagnostic> issueConsummer = captor.getValue();

    OmnisharpDiagnostic diag = new OmnisharpDiagnostic();
    diag.id = "S12345";
    diag.line = 1;
    diag.column = 1;
    diag.endLine = 1;
    diag.endColumn = 5;
    diag.text = "Don't do this";

    issueConsummer.accept(diag);

    assertThat(sensorContext.allIssues()).extracting(Issue::ruleKey, i -> i.primaryLocation().inputComponent(), i -> i.primaryLocation().message(),
      i -> i.primaryLocation().textRange().start().line(),
      i -> i.primaryLocation().textRange().start().lineOffset(),
      i -> i.primaryLocation().textRange().end().line(),
      i -> i.primaryLocation().textRange().end().lineOffset())
      .containsOnly(tuple(ruleKey, file, "Don't do this", 1, 0, 1, 4));
  }

  @Test
  void testLoadRules() {
    assertThat(underTest.getAllRulesKeys()).hasSize(395);
  }

}
